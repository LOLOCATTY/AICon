#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace AICon.Agent
{
    // Speaks the OpenAI /chat/completions "tools" dialect — the de-facto standard shared by a huge
    // range of backends. One provider, many models, selected purely by baseUrl + model + key:
    //   * Local  : Ollama (http://localhost:11434/v1), LM Studio (http://localhost:1234/v1), vLLM…
    //   * Cloud  : DeepSeek (https://api.deepseek.com), ChatGPT (https://api.openai.com/v1)…
    // This is why it is the first provider we build: it alone covers the whole local-model roadmap
    // plus the mainstream cloud alternatives.
    public sealed class OpenAiCompatibleProvider : IProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly bool _isLocal;   // localhost endpoint (Ollama/LM Studio) → small-model reliability tweaks

        public string Name => "openai-compatible (" + _model + ")";
        public Action<string>? Status { get; set; }

        public OpenAiCompatibleProvider(Config cfg)
        {
            _model = cfg.Model;
            _endpoint = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
            _isLocal = _endpoint.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
                    || _endpoint.Contains("127.0.0.1");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

            string? key = cfg.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        public async Task<AssistantTurn> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, JsonArray tools, CancellationToken ct)
        {
            var request = new JsonObject
            {
                ["model"] = _model,
                ["messages"] = BuildMessages(messages),
                ["stream"] = false
            };
            // Omit the tools field entirely when none are offered (the force-summary close uses an
            // empty list to make the model answer in plain text; some endpoints reject "tools": []).
            if (tools.Count > 0)
            {
                request["tools"] = BuildTools(tools);
                request["tool_choice"] = "auto";
            }
            // Small local models call tools far more consistently at temperature 0 (their default of
            // ~0.7 makes tool selection a dice roll). Cloud endpoints keep their tuned defaults.
            if (_isLocal) request["temperature"] = 0;

            using (var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        "Model endpoint returned " + (int)resp.StatusCode + " " + resp.ReasonPhrase +
                        ". Endpoint: " + _endpoint + ". Body: " + Truncate(body, 600));

                JsonNode? root = JsonNode.Parse(body);
                if (root == null)
                    throw new InvalidOperationException("Empty response from model. Body: " + Truncate(body, 300));

                JsonNode? message = root["choices"]?[0]?["message"];
                if (message == null)
                    throw new InvalidOperationException("No message in model response. Body: " + Truncate(body, 600));

                string? text = message["content"]?.GetValue<string>();

                var calls = new List<ToolCall>();
                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    int i = 0;
                    foreach (JsonNode? tc in toolCalls)
                    {
                        if (tc == null) continue;
                        string id = tc["id"]?.GetValue<string>() ?? ("call_" + i);
                        string name = tc["function"]?["name"]?.GetValue<string>() ?? "";
                        if (name.Length == 0) continue;

                        // arguments is normally a JSON string, but some local models emit a raw object.
                        JsonNode? argNode = tc["function"]?["arguments"];
                        string argsJson;
                        if (argNode is JsonValue v && v.TryGetValue(out string? s))
                            argsJson = string.IsNullOrWhiteSpace(s) ? "{}" : s!;
                        else if (argNode == null)
                            argsJson = "{}";
                        else
                            argsJson = argNode.ToJsonString();

                        calls.Add(new ToolCall(id, name, argsJson));
                        i++;
                    }
                }

                // Fallback for weaker local models (common with Ollama): instead of the structured
                // `tool_calls` field, they dump the call as plain JSON in `content`, e.g.
                //   {"name":"create_sheet","arguments":{"name":"Hello"}}
                // If we offered tools, got no structured call, but the content is a tool-call-shaped
                // JSON naming a REAL tool, recover it so the agent can actually run it.
                if (calls.Count == 0 && !string.IsNullOrWhiteSpace(text))
                {
                    List<ToolCall> recovered = TryRecoverToolCallsFromText(text!, tools);
                    if (recovered.Count > 0)
                    {
                        calls.AddRange(recovered);
                        text = null;   // the JSON was the call itself, not a message to show the user
                    }
                }

                return new AssistantTurn(text, calls);
            }
        }

        // Convert our neutral conversation into OpenAI wire messages.
        private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
        {
            var arr = new JsonArray();
            foreach (ChatMessage m in messages)
            {
                switch (m.Role)
                {
                    case Role.System:
                        arr.Add(new JsonObject { ["role"] = "system", ["content"] = m.Content ?? "" });
                        break;
                    case Role.User:
                        if (m.Attachments == null || m.Attachments.Count == 0)
                        {
                            arr.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" });
                            break;
                        }
                        // Vision form: content becomes an array of text + image_url (data URI) parts.
                        // Only vision-capable models can use the images; PDFs are NOT supported on
                        // this wire — flag them in text so the model can tell the user.
                        string userText = m.Content ?? "";
                        var contentParts = new JsonArray();
                        foreach (ChatAttachment att in m.Attachments)
                        {
                            if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                                contentParts.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = "data:" + att.MimeType + ";base64," + att.Base64Data
                                    }
                                });
                            else
                                userText += "\n[Attached file '" + att.FileName + "' (" + att.MimeType +
                                            ") cannot be read by this model — PDFs need the Gemini agent.]";
                        }
                        contentParts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = userText });
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = contentParts });
                        break;
                    case Role.Tool:
                        arr.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = m.ToolCallId ?? "",
                            ["name"] = m.Name ?? "",
                            ["content"] = m.Content ?? ""
                        });
                        break;
                    case Role.Assistant:
                        var am = new JsonObject { ["role"] = "assistant", ["content"] = m.Content ?? "" };
                        if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                        {
                            var tcs = new JsonArray();
                            foreach (ToolCall c in m.ToolCalls)
                                tcs.Add(new JsonObject
                                {
                                    ["id"] = c.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = c.Name,
                                        ["arguments"] = c.ArgumentsJson
                                    }
                                });
                            am["tool_calls"] = tcs;
                        }
                        arr.Add(am);
                        break;
                }
            }
            return arr;
        }

        // MCP tool shape { name, description, inputSchema } -> OpenAI { type, function{ name, description, parameters } }.
        private static JsonArray BuildTools(JsonArray tools)
        {
            var arr = new JsonArray();
            foreach (JsonNode? t in tools)
            {
                if (t == null) continue;
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t["name"]?.DeepClone(),
                        ["description"] = t["description"]?.DeepClone(),
                        ["parameters"] = t["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
                    }
                });
            }
            return arr;
        }

        // Recover tool calls that a model emitted as plain-text JSON in its content instead of the
        // proper `tool_calls` field. Conservative: only fires when the text parses as JSON and names a
        // tool we actually offered, so ordinary answers (even JSON-looking ones) are left untouched.
        // Accepts a single object or an array, and the shapes {name,arguments} / {name,parameters} /
        // {function:{name,arguments}}. Handles ```json code fences.
        private static List<ToolCall> TryRecoverToolCallsFromText(string content, JsonArray tools)
        {
            var result = new List<ToolCall>();
            string s = StripCodeFences(content).Trim();
            if (s.Length == 0) return result;

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? t in tools)
            {
                string? n = t?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(n)) known.Add(n!);
            }
            if (known.Count == 0) return result;

            // Shape B (seen from qwen): the bare text `tool_name {json}` or just `tool_name` on one
            // line — no JSON wrapper at all. Only accepted when the name is EXACTLY a known tool.
            System.Text.RegularExpressions.Match bare = System.Text.RegularExpressions.Regex.Match(
                s, @"^([A-Za-z0-9_]+)\s*(\{[\s\S]*\})?\s*$");
            if (bare.Success && known.Contains(bare.Groups[1].Value))
            {
                string bareArgs = bare.Groups[2].Success ? bare.Groups[2].Value : "{}";
                try { JsonNode.Parse(bareArgs); } catch { bareArgs = "{}"; }
                result.Add(new ToolCall("call_0", bare.Groups[1].Value, bareArgs));
                return result;
            }

            // Everything else: one JSON object, a JSON array, MULTIPLE objects on separate lines
            // (JSONL — how qwen emits a compound request), or object(s) embedded in prose (llama).
            // ParseCandidates handles all four; order is preserved so dependent steps (create a
            // template, then a filter inside it) execute in the sequence the model intended.
            List<JsonNode?> items = ParseCandidates(s);
            if (items.Count == 0) return result;
            int i = 0;
            foreach (JsonNode? item in items)
            {
                try
                {
                    JsonObject? obj = item as JsonObject;
                    if (obj == null) continue;

                    JsonObject fn = obj["function"] as JsonObject ?? obj;   // {function:{…}} or flat
                    // Models name the tool under several keys: name / function_name / tool, or
                    // {"function":"tool_name", ...} with function as a plain string.
                    string? name = StringProp(fn, "name") ?? StringProp(fn, "function_name") ?? StringProp(fn, "tool");
                    if (name == null && obj["function"] is JsonValue fv && fv.TryGetValue(out string? fs)) name = fs;
                    if (string.IsNullOrEmpty(name)) continue;
                    // Accept a known tool outright. Also accept an UNKNOWN name when the object is
                    // unmistakably tool-call-shaped (snake_case identifier + an arguments/parameters
                    // key): local models sometimes invent tools (e.g. create_view_templates), and
                    // dispatching that lets the executor's "Unknown tool" error flow back to the
                    // model as feedback so it can correct itself — far better than dumping raw JSON
                    // at the user.
                    bool knownTool = known.Contains(name!);
                    bool toolShaped = (fn["arguments"] != null || fn["parameters"] != null) &&
                        System.Text.RegularExpressions.Regex.IsMatch(name!, "^[a-z][a-z0-9_]{2,}$");
                    if (!knownTool && !toolShaped) continue;

                    JsonNode? args = fn["arguments"] ?? fn["parameters"];
                    string argsJson;
                    if (args is JsonValue av && av.TryGetValue(out string? astr))
                        argsJson = string.IsNullOrWhiteSpace(astr) ? "{}" : astr!;
                    else
                        argsJson = args != null ? args.ToJsonString() : "{}";

                    result.Add(new ToolCall("call_" + i, name!, argsJson));
                    i++;
                }
                catch { /* skip a malformed candidate rather than fail the whole turn */ }
            }
            return result;
        }

        private static string? StringProp(JsonObject obj, string key) =>
            obj[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        // Turn the model's text into candidate JSON nodes. Tries a clean whole-string parse first
        // (single object, or an array whose elements are the candidates); failing that, scans for
        // every balanced {...} in the text — which covers JSONL (one call per line) and objects
        // embedded in prose — in order of appearance.
        private static List<JsonNode?> ParseCandidates(string s)
        {
            var items = new List<JsonNode?>();
            try
            {
                JsonNode? whole = JsonNode.Parse(s);
                if (whole is JsonArray arr) { foreach (JsonNode? n in arr) items.Add(n); return items; }
                if (whole != null) { items.Add(whole); return items; }
            }
            catch { /* fall through to the scanner */ }

            int pos = 0;
            while (true)
            {
                string? objStr = ExtractJsonObject(s, pos, out int end);
                if (objStr == null) break;
                try
                {
                    JsonNode? n = JsonNode.Parse(objStr);
                    if (n != null) items.Add(n);
                }
                catch { /* skip malformed candidate */ }
                pos = end;
            }
            return items;
        }

        // Next balanced {...} at or after 'from', honouring strings/escapes; null if none closes.
        // 'end' is the index just past the object, for resuming the scan.
        private static string? ExtractJsonObject(string s, int from, out int end)
        {
            end = s.Length;
            int start = s.IndexOf('{', from);
            if (start < 0) return null;
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    end = i + 1;
                    return s.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        // Strip a leading ```json / ``` fence and trailing ``` so the inner JSON can be parsed.
        private static string StripCodeFences(string s)
        {
            s = s.Trim();
            if (!s.StartsWith("```")) return s;
            int firstNl = s.IndexOf('\n');
            if (firstNl < 0) return s;
            s = s.Substring(firstNl + 1);
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s.Substring(0, lastFence);
            return s;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
