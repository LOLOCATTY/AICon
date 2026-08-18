#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using AICon.Shared;

namespace AICon.Agent
{
    // Executes tools by POSTing to the AICon Revit add-in's localhost HTTP bridge — the contract the
    // MCP server also uses: POST { "tool": name, "args": {...} } -> { ok, data, error }. Used by the
    // console host (a separate process). The in-Revit panel uses InProcessToolExecutor instead.
    public sealed class HttpToolExecutor : IToolExecutor
    {
        private const string BridgeUrl = "http://localhost:55234/";
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        public async Task<string> ExecuteAsync(string tool, string argumentsJson, CancellationToken ct)
        {
            JsonNode? args;
            try { args = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson); }
            catch (Exception ex) { return "Error: could not parse arguments as JSON: " + ex.Message; }

            var payload = new JsonObject { ["tool"] = tool, ["args"] = args?.DeepClone() ?? new JsonObject() };
            HttpResponseMessage response;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BridgeUrl))
                {
                    request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                    // Read fresh every call — see shared/BridgeToken.cs for why this isn't cached.
                    string? token = BridgeToken.Load();
                    if (!string.IsNullOrEmpty(token)) request.Headers.Add("X-AICon-Token", token);
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                return "Could not reach Revit. Make sure: (1) Revit is running, (2) a project is open, " +
                       "(3) the AICon add-in loaded at startup (look for the AICon ribbon tab). " +
                       "Details: " + ex.Message;
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return BridgeResult.ToText(body);
        }
    }

    // Shared interpretation of a Revit bridge JSON reply { ok, data, error } into text for the model.
    internal static class BridgeResult
    {
        public static string ToText(string body)
        {
            try
            {
                JsonNode? parsed = JsonNode.Parse(body);
                bool ok = parsed?["ok"]?.GetValue<bool>() ?? false;
                if (!ok)
                    return "Revit error: " + (parsed?["error"]?.GetValue<string>() ?? body);

                JsonNode? data = parsed!["data"];

                // Image results come back as base64. A text chat can't show them; hand the model a
                // short note so it knows the render succeeded.
                if (data is JsonObject obj && obj["image_base64"] is JsonNode img)
                {
                    int approxBytes = (img.GetValue<string>().Length * 3) / 4;
                    string dims = obj["width"] is JsonNode w && obj["height"] is JsonNode h
                        ? " (" + w + "x" + h + "px)" : "";
                    return "[Revit rendered a view image" + dims + ", ~" + approxBytes +
                           " bytes. The image is not shown in this text chat.]";
                }

                return data?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
            }
            catch (JsonException)
            {
                return "Unexpected response from Revit bridge: " + body;
            }
        }
    }
}
