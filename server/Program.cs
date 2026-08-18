using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AICon.Shared;

// AICon MCP stdio server: bridges Claude Desktop <-> the AICon Revit add-in (localhost HTTP).
// Protocol: JSON-RPC 2.0, one JSON message per line on stdin/stdout. Logs go to stderr only.

const string BridgeUrl = "http://localhost:55234/";
// Read from the assembly (stamped by AIConServer.csproj's <Version>) instead of a second hand-typed
// literal — this file used to hardcode "2.1.2" separately from the .csproj and the two would drift.
string serverVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

Console.OutputEncoding = new UTF8Encoding(false);
var stdout = Console.Out;
var stdin = Console.In;

void Log(string msg) => Console.Error.WriteLine($"[aicon] {msg}");

void Send(JsonObject message)
{
    stdout.WriteLine(message.ToJsonString());
    stdout.Flush();
}

void SendResult(JsonNode id, JsonNode result) =>
    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

void SendError(JsonNode? id, int code, string message) =>
    Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    });

JsonNode ToolResultText(string text, bool isError = false) =>
    new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

async Task<JsonNode> CallRevit(string tool, JsonNode? arguments)
{
    JsonObject payload = new() { ["tool"] = tool, ["args"] = arguments?.DeepClone() ?? new JsonObject() };
    HttpResponseMessage response;
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BridgeUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        // Read fresh every call, not cached: this process can start before Revit does, in which case
        // the token file doesn't exist yet — re-reading picks up a Revit that starts later without
        // needing this process restarted.
        string? token = BridgeToken.Load();
        if (!string.IsNullOrEmpty(token)) request.Headers.Add("X-AICon-Token", token);
        response = await http.SendAsync(request);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return ToolResultText(
            "Could not reach Revit. Make sure: (1) Revit is running, (2) a project is open, " +
            "(3) the AICon add-in loaded at startup (look for the AICon ribbon tab). " +
            $"Details: {ex.Message}", isError: true);
    }

    string body = await response.Content.ReadAsStringAsync();
    try
    {
        JsonNode? parsed = JsonNode.Parse(body);
        bool ok = parsed?["ok"]?.GetValue<bool>() ?? false;
        if (!ok)
            return ToolResultText($"Revit error: {parsed?["error"]?.GetValue<string>() ?? body}", isError: true);

        JsonNode? data = parsed!["data"];

        // Image results come back as base64; hand them to Claude as a real image.
        if (data is JsonObject obj && obj["image_base64"] is JsonNode img)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = img.GetValue<string>(),
                        ["mimeType"] = "image/png"
                    }),
                ["isError"] = false
            };
        }

        return ToolResultText(data?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
    }
    catch (JsonException)
    {
        return ToolResultText($"Unexpected response from Revit bridge: {body}", isError: true);
    }
}

Log("AICon MCP server starting, bridge target " + BridgeUrl);
Tools.BuildToolList(); // populate Tools.Names before any tools/call arrives

string? line;
while ((line = await stdin.ReadLineAsync()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonNode? message;
    try { message = JsonNode.Parse(line); }
    catch (JsonException ex) { Log("bad json: " + ex.Message); continue; }
    if (message is null) continue;

    string method = message["method"]?.GetValue<string>() ?? "";
    JsonNode? id = message["id"];
    bool isNotification = id is null;

    try
    {
        switch (method)
        {
            case "initialize":
                string requested = message["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
                SendResult(id!, new JsonObject
                {
                    ["protocolVersion"] = requested,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "aicon", ["version"] = serverVersion },
                    ["instructions"] =
                        "AICon connects you to the user's live Autodesk Revit session. " +
                        "All lengths and coordinates are in MILLIMETERS, angles in degrees, areas in m2. " +
                        "PERFORMANCE — THIS IS CRITICAL: NEVER make repeated individual tool calls for bulk work. " +
                        "Creating/modifying more than ~3 things? Put ALL operations in ONE 'batch' call " +
                        "(e.g. 36 create_floor_plan + 36 create_sheet + 36 place_view_on_sheet = ONE batch call, runs in seconds). " +
                        "Repeated individual calls are 10-100x slower because each one is a full AI round-trip plus a separate Revit transaction. " +
                        "For logic batch can't express (loops with computed values, reading then writing), write ONE run_code script instead. " +
                        "Good first steps: get_project_info, then list_categories or list_levels. " +
                        "Use export_view_image to actually SEE the model, and select/show/isolate/color tools to point things out to the user. " +
                        "If no dedicated tool fits, use run_code (C# 5 inside Revit) — it can do anything the Revit API can. " +
                        "Every edit (a batch counts as one) is one undoable Revit transaction. " +
                        "Confirm with the user before deleting elements or running destructive code."
                });
                break;

            case "notifications/initialized":
            case "notifications/cancelled":
                break;

            case "ping":
                SendResult(id!, new JsonObject());
                break;

            case "tools/list":
                SendResult(id!, new JsonObject { ["tools"] = Tools.BuildToolList() });
                break;

            case "tools/call":
                string toolName = message["params"]?["name"]?.GetValue<string>() ?? "";
                JsonNode? arguments = message["params"]?["arguments"];
                if (!Tools.Names.Contains(toolName))
                {
                    SendResult(id!, ToolResultText($"Unknown tool '{toolName}'.", isError: true));
                    break;
                }
                SendResult(id!, await CallRevit(toolName, arguments));
                break;

            default:
                if (!isNotification)
                    SendError(id, -32601, $"Method not found: {method}");
                break;
        }
    }
    catch (Exception ex)
    {
        Log($"error handling {method}: {ex}");
        if (!isNotification) SendError(id, -32603, ex.Message);
    }
}

Log("stdin closed, exiting");
