#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace AICon.Agent
{
    // Google Gemini backend. Gemini's generateContent API differs from OpenAI's in three ways this
    // provider bridges:
    //   * roles are only "user" / "model" (no system/tool) — the system prompt goes in
    //     systemInstruction, and tool results come back as role "user" functionResponse parts;
    //   * tool calls arrive as functionCall parts with args as a JSON *object* (not a string), and
    //     thinking models attach a thoughtSignature that MUST be echoed back;
    //   * tools are declared under { functionDeclarations: [...] } with plain JSON Schema parameters.
    // Everything below IProvider (tools, executor, loop) is reused unchanged.
    public sealed class GeminiProvider : IProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;

        public string Name => "gemini (" + _model + ")";
        public Action<string>? Status { get; set; }

        private const int MaxRateLimitRetries = 3;

        public GeminiProvider(Config cfg)
        {
            _model = cfg.Model;
            // Default base is the public Generative Language API. Also fall back to it when baseUrl is
            // still an OpenAI-local default (Ollama :11434 / LM Studio :1234) left over from switching
            // provider — but honour any other explicit baseUrl (Vertex, a proxy, or a test endpoint).
            bool looksLikeOpenAiLocalDefault = cfg.BaseUrl.Contains("11434") || cfg.BaseUrl.Contains(":1234");
            string baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) || looksLikeOpenAiLocalDefault
                ? "https://generativelanguage.googleapis.com/v1beta"
                : cfg.BaseUrl.TrimEnd('/');
            _endpoint = baseUrl + "/models/" + _model + ":generateContent";

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            string? key = cfg.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "Gemini needs an API key. Set \"apiKey\" or \"apiKeyEnv\" in aiconagent.json " +
                    "(get a free key at https://aistudio.google.com/apikey).");
            // Header auth keeps the key out of the URL/query string.
            _http.DefaultRequestHeaders.Add("x-goog-api-key", key);
        }

        public async Task<AssistantTurn> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, JsonArray tools, CancellationToken ct)
        {
            var request = new JsonObject
            {
                ["contents"] = BuildContents(messages)
            };
            // Omit tools entirely when none are offered (force-summary close) — Gemini rejects an
            // empty functionDeclarations array.
            if (tools.Count > 0)
                request["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = BuildDeclarations(tools) });
            string? system = messages.FirstOrDefault(m => m.Role == Role.System)?.Content;
            if (!string.IsNullOrWhiteSpace(system))
                request["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };

            string payload = request.ToJsonString();

            // Send, with automatic back-off on 429 (free-tier rate limits: one API call per tool round
            // adds up fast). We honour the retryDelay Gemini reports, so the loop just pauses and
            // continues instead of failing the whole turn.
            string body;
            int attempt = 0;
            while (true)
            {
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                using (HttpResponseMessage resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false))
                {
                    body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode) break;

                    if ((int)resp.StatusCode == 429 && attempt < MaxRateLimitRetries)
                    {
                        int waitSec = ParseRetryDelaySeconds(body);
                        attempt++;
                        Status?.Invoke("Gemini rate limit reached — waiting " + waitSec + "s (retry " +
                                       attempt + "/" + MaxRateLimitRetries + ")…");
                        await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
                        continue;
                    }

                    if ((int)resp.StatusCode == 429)
                        throw new HttpRequestException(
                            "Gemini rate limit (429): the free tier allows only a few requests per minute, " +
                            "and this model was exhausted. Wait a minute and retry, switch \"model\" in " +
                            "aiconagent.json to a higher-limit one (e.g. gemini-2.0-flash), or enable billing. " +
                            "Body: " + Truncate(body, 300));

                    throw new HttpRequestException(
                        "Gemini returned " + (int)resp.StatusCode + " " + resp.ReasonPhrase +
                        ". Endpoint: " + _endpoint + ". Body: " + Truncate(body, 600));
                }
            }

            {
                JsonNode? root = JsonNode.Parse(body);
                if (root == null)
                    throw new InvalidOperationException("Empty response from Gemini. Body: " + Truncate(body, 300));

                JsonNode? parts = root["candidates"]?[0]?["content"]?["parts"];
                if (!(parts is JsonArray partsArr))
                {
                    // No parts usually means the turn was blocked or empty — surface why.
                    string? finish = root["candidates"]?[0]?["finishReason"]?.GetValue<string>();
                    string? block = root["promptFeedback"]?["blockReason"]?.GetValue<string>();
                    throw new InvalidOperationException(
                        "Gemini returned no content (finishReason=" + (finish ?? "?") +
                        ", blockReason=" + (block ?? "none") + "). Body: " + Truncate(body, 400));
                }

                var textSb = new StringBuilder();
                var calls = new List<ToolCall>();
                int i = 0;
                foreach (JsonNode? part in partsArr)
                {
                    if (part == null) continue;
                    if (part["text"] is JsonNode t)
                        textSb.Append(t.GetValue<string>());
                    else if (part["functionCall"] is JsonNode fc)
                    {
                        string name = fc["name"]?.GetValue<string>() ?? "";
                        if (name.Length == 0) continue;
                        string argsJson = fc["args"]?.ToJsonString() ?? "{}";
                        // thoughtSignature is a sibling of functionCall on the part; capture it so we can
                        // echo it back next turn (thinking models require it).
                        string? sig = part["thoughtSignature"]?.GetValue<string>();
                        calls.Add(new ToolCall("call_" + i, name, argsJson, sig));
                        i++;
                    }
                }

                string? text = textSb.Length > 0 ? textSb.ToString() : null;
                return new AssistantTurn(text, calls);
            }
        }

        // Neutral history -> Gemini contents. System is lifted out; consecutive tool results are
        // coalesced into one role:"user" content (Gemini pairs one model turn of N functionCalls
        // with one user turn of N functionResponses).
        private static JsonArray BuildContents(IReadOnlyList<ChatMessage> messages)
        {
            var contents = new JsonArray();
            int i = 0;
            while (i < messages.Count)
            {
                ChatMessage m = messages[i];
                switch (m.Role)
                {
                    case Role.System:
                        i++;
                        break;

                    case Role.User:
                        var userParts = new JsonArray { new JsonObject { ["text"] = m.Content ?? "" } };
                        // Attachments (images / PDFs) ride along as inline_data parts — Gemini reads
                        // both natively.
                        if (m.Attachments != null)
                            foreach (ChatAttachment att in m.Attachments)
                                userParts.Add(new JsonObject
                                {
                                    ["inline_data"] = new JsonObject
                                    {
                                        ["mime_type"] = att.MimeType,
                                        ["data"] = att.Base64Data
                                    }
                                });
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = userParts });
                        i++;
                        break;

                    case Role.Assistant:
                        var modelParts = new JsonArray();
                        if (!string.IsNullOrWhiteSpace(m.Content))
                            modelParts.Add(new JsonObject { ["text"] = m.Content });
                        if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                            foreach (ToolCall c in m.ToolCalls)
                            {
                                JsonNode argsNode;
                                try { argsNode = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) ?? new JsonObject(); }
                                catch { argsNode = new JsonObject(); }
                                var fcPart = new JsonObject
                                {
                                    ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = argsNode }
                                };
                                // Echo the thinking-model signature back on the same part, or Gemini 400s.
                                if (!string.IsNullOrEmpty(c.ThoughtSignature))
                                    fcPart["thoughtSignature"] = c.ThoughtSignature;
                                modelParts.Add(fcPart);
                            }
                        contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
                        i++;
                        break;

                    case Role.Tool:
                        var responseParts = new JsonArray();
                        while (i < messages.Count && messages[i].Role == Role.Tool)
                        {
                            ChatMessage tm = messages[i];
                            responseParts.Add(new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["name"] = tm.Name ?? "",
                                    // response must be an object; wrap the tool's text result.
                                    ["response"] = new JsonObject { ["result"] = tm.Content ?? "" }
                                }
                            });
                            i++;
                        }
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responseParts });
                        break;
                }
            }
            return contents;
        }

        // MCP tool shape { name, description, inputSchema } -> Gemini { name, description, parameters }.
        private static JsonArray BuildDeclarations(JsonArray tools)
        {
            var arr = new JsonArray();
            foreach (JsonNode? t in tools)
            {
                if (t == null) continue;
                var decl = new JsonObject
                {
                    ["name"] = t["name"]?.DeepClone(),
                    ["description"] = t["description"]?.DeepClone()
                };
                // Gemini rejects an empty parameters object with no properties; only attach a schema
                // when the tool actually takes arguments.
                if (t["inputSchema"] is JsonObject schema &&
                    schema["properties"] is JsonObject props && props.Count > 0)
                    decl["parameters"] = schema.DeepClone();
                arr.Add(decl);
            }
            return arr;
        }

        // Pull the retry delay Gemini suggests out of a 429 body ("retryDelay": "54s", or
        // "...retry in 54.9...s"). Falls back to 20s, and clamps to a sane 1..60s window.
        private static int ParseRetryDelaySeconds(string body)
        {
            int seconds = 20;
            Match m = Regex.Match(body, "retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s");
            if (!m.Success) m = Regex.Match(body, "retry in ([\\d.]+)s");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double s))
                seconds = (int)Math.Ceiling(s);
            if (seconds < 1) seconds = 1;
            if (seconds > 60) seconds = 60;
            return seconds;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
