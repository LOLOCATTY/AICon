#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AICon.Agent
{
    // Executes one tool and returns a text result the model can read. Two implementations:
    //   HttpToolExecutor       — POSTs to the Revit bridge (console host, separate process)
    //   InProcessToolExecutor  — dispatches directly on Revit's UI thread (in-Revit chat panel)
    public interface IToolExecutor
    {
        Task<string> ExecuteAsync(string tool, string argumentsJson, CancellationToken ct);
    }

    // The conversation loop, shared by the console host and the in-Revit panel. It owns the
    // message history and drives provider ↔ tool rounds; callers observe progress through events
    // (so a console can print and a WPF panel can update its UI from the very same loop).
    public sealed class Agent
    {
        private readonly IProvider _provider;
        private readonly JsonArray _tools;
        private readonly IToolExecutor _executor;
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private readonly int _maxToolRoundsPerTurn;

        public event Action<string>? AssistantText;        // model's natural-language reply
        public event Action<string, string>? ToolStarted;  // (tool name, compact args)
        public event Action<string>? ToolFinished;         // (short preview of the result)
        public event Action<string>? Notice;               // loop status / limits

        public IProvider Provider => _provider;

        // contextProvider (optional): fetches a live project snapshot; its text is appended to the
        // system prompt right before the FIRST message, so the model knows the real names of levels,
        // views and templates instead of guessing. seedMessages (optional): a canned successful
        // exchange placed at the start of history — few-shot teaching for small local models.
        private readonly Func<Task<string?>>? _contextProvider;
        private bool _contextInjected;

        public Agent(IProvider provider, JsonArray tools, IToolExecutor executor,
                     string systemPrompt, int maxToolRoundsPerTurn = 25,
                     IEnumerable<ChatMessage>? seedMessages = null,
                     Func<Task<string?>>? contextProvider = null)
        {
            _provider = provider;
            _tools = tools;
            _executor = executor;
            _maxToolRoundsPerTurn = maxToolRoundsPerTurn;
            _contextProvider = contextProvider;
            _messages.Add(ChatMessage.System(systemPrompt));
            if (seedMessages != null) _messages.AddRange(seedMessages);
        }

        public Task SendAsync(string userMessage, CancellationToken ct)
            => SendAsync(userMessage, null, ct);

        // One user turn: keep asking the model until it stops requesting tools (or the cap hits).
        // Attachments (images/PDFs from the chat's attach button) ride on the user message.
        public async Task SendAsync(string userMessage, List<ChatAttachment>? attachments, CancellationToken ct)
        {
            // Lazily fetch the live project snapshot once and fold it into the system prompt.
            // Appending to message[0] (not adding a second system message) keeps every provider
            // happy — Gemini only reads the first system message.
            if (!_contextInjected && _contextProvider != null)
            {
                _contextInjected = true;   // one attempt only; a failed snapshot is not fatal
                try
                {
                    string? ctx = await _contextProvider().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ctx))
                        _messages[0].Content += "\n\nLIVE PROJECT CONTEXT (real names — use them EXACTLY):\n" + ctx;
                }
                catch { }
            }

            _messages.Add(new ChatMessage
            {
                Role = Role.User,
                Content = userMessage,
                Attachments = attachments != null && attachments.Count > 0 ? attachments : null
            });

            // Canonical name+args of every call already executed this turn. Small models sometimes
            // forget a call succeeded and issue the very same one again in a later round (which is
            // how a user once got two identical view filters). A repeat is skipped with a firm
            // "already done" message instead of re-executing; identical calls WITHIN one round are
            // left alone (repeating an op several times in one batch is usually intentional).
            var executed = new HashSet<string>();
            int duplicateStrikes = 0;
            string? lastToolName = null;     // for echo detection: the previous call and its result
            JsonNode? lastToolResult = null;

            for (int round = 0; round < _maxToolRoundsPerTurn; round++)
            {
                AssistantTurn turn = await _provider.CompleteAsync(_messages, _tools, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(turn.Text))
                    AssistantText?.Invoke(turn.Text!.Trim());

                if (!turn.WantsTools)
                {
                    _messages.Add(new ChatMessage { Role = Role.Assistant, Content = turn.Text ?? "" });
                    return;
                }

                // Record the assistant's tool-call turn, then run each call and feed results back.
                _messages.Add(new ChatMessage
                {
                    Role = Role.Assistant,
                    Content = turn.Text ?? "",
                    ToolCalls = turn.ToolCalls
                });

                var roundKeys = new List<string>();
                foreach (ToolCall call in turn.ToolCalls)
                {
                    string key = call.Name + "|" + CanonicalJson(call.ArgumentsJson);
                    if (executed.Contains(key))
                    {
                        duplicateStrikes++;
                        ToolStarted?.Invoke(call.Name, "(duplicate of a completed call — skipped)");
                        string skipMsg =
                            "SKIPPED: this exact call already succeeded earlier in this turn — see its result above. " +
                            "Do NOT repeat it. If everything the user asked for is done, reply with a short summary " +
                            "and no more tool calls.";
                        ToolFinished?.Invoke("skipped duplicate");
                        _messages.Add(new ChatMessage
                        {
                            Role = Role.Tool,
                            ToolCallId = call.Id,
                            Name = call.Name,
                            Content = skipMsg
                        });
                        continue;
                    }
                    // ECHO GUARD: models also loop by re-calling the SAME tool with the PREVIOUS
                    // RESULT's data as arguments (e.g. create_sheet again with the number Revit just
                    // assigned) — args differ every round, so the duplicate check above never fires.
                    // If every argument value is copied verbatim from the last result of the same
                    // tool, this is an echo, not a new instruction: skip it and force the summary.
                    if (call.Name == lastToolName && ArgsEchoResult(call.ArgumentsJson, lastToolResult))
                    {
                        duplicateStrikes++;
                        ToolStarted?.Invoke(call.Name, "(echo of the previous result — skipped)");
                        ToolFinished?.Invoke("skipped echo");
                        _messages.Add(new ChatMessage
                        {
                            Role = Role.Tool,
                            ToolCallId = call.Id,
                            Name = call.Name,
                            Content = "SKIPPED: these arguments just repeat the PREVIOUS result's data — that work is " +
                                      "already DONE. Do not call this tool again; reply with a short summary."
                        });
                        continue;
                    }
                    roundKeys.Add(key);

                    ToolStarted?.Invoke(call.Name, Compact(call.ArgumentsJson));

                    string result;
                    try
                    {
                        result = await _executor.ExecuteAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result = "Tool execution error: " + ex.Message;
                    }

                    ToolFinished?.Invoke(Preview(result));
                    _messages.Add(new ChatMessage
                    {
                        Role = Role.Tool,
                        ToolCallId = call.Id,
                        Name = call.Name,
                        Content = result
                    });

                    lastToolName = call.Name;
                    try { lastToolResult = JsonNode.Parse(result); } catch { lastToolResult = null; }

                    // VISION: look_at_view exports the view as an image — load it and show it to the
                    // model as an inline image so it can actually SEE the view.
                    if (call.Name == "look_at_view")
                        TryAttachImageFromResult(result);
                }
                foreach (string k in roundKeys) executed.Add(k);

                // A duplicate means the model has lost the thread — it is re-doing finished work.
                // Stop offering tools and ask for a plain-text closing summary instead of letting
                // it keep flailing (and creating stray elements).
                if (duplicateStrikes > 0)
                {
                    await ForceFinalSummary(ct).ConfigureAwait(false);
                    return;
                }
            }

            Notice?.Invoke($"Stopped after {_maxToolRoundsPerTurn} tool rounds without a final answer.");
        }

        // True when EVERY argument key exists in the previous result with an identical value —
        // i.e. the model copied the result back as a "new" call. A genuinely new instruction always
        // carries at least one value that was not in the last result.
        private static bool ArgsEchoResult(string argsJson, JsonNode? lastResult)
        {
            var res = lastResult as JsonObject;
            if (res == null) return false;
            JsonObject? args;
            try { args = JsonNode.Parse(argsJson) as JsonObject; } catch { return false; }
            if (args == null || args.Count == 0) return false;
            foreach (KeyValuePair<string, JsonNode?> kv in args)
            {
                JsonNode? rv = res[kv.Key];
                if (rv == null) return false;
                string a = kv.Value?.ToJsonString() ?? "null";
                string r = rv.ToJsonString();
                if (!string.Equals(a, r, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // If a look_at_view result names an exported image file, feed the image itself back into the
        // conversation as a user-message attachment (both provider dialects accept images only on
        // user messages, not on tool results). Silent no-op when anything is missing or too big.
        private void TryAttachImageFromResult(string result)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(result);
                string? path = node?["path"]?.GetValue<string>();
                if (path == null || !System.IO.File.Exists(path)) return;
                var info = new System.IO.FileInfo(path);
                if (info.Length > 6_000_000) { Notice?.Invoke("view image too large to show the model (>6 MB)"); return; }
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png";
                string b64 = Convert.ToBase64String(System.IO.File.ReadAllBytes(path));
                _messages.Add(new ChatMessage
                {
                    Role = Role.User,
                    Content = "(Here is the exported image of that view — analyze what you actually see in it.)",
                    Attachments = new List<ChatAttachment>
                    {
                        new ChatAttachment { FileName = System.IO.Path.GetFileName(path), MimeType = mime, Base64Data = b64 }
                    }
                });
            }
            catch { /* not JSON / unreadable file — the text result alone still stands */ }
        }

        // Close the turn with a text-only completion: no tools are offered, so the model must answer
        // in plain language. Used when the loop detects the model repeating completed calls.
        private async Task ForceFinalSummary(CancellationToken ct)
        {
            // Deliberately neutral wording: the loop may have stopped after FAILURES (unknown tools,
            // errors), so the model must report the actual outcome, not claim success.
            _messages.Add(ChatMessage.User(
                "Stop. Based ONLY on the tool results above, reply with 1-2 short sentences in the " +
                "user's language stating what actually SUCCEEDED and what FAILED (with the reason). " +
                "Do not claim anything succeeded unless a result above proves it. Do not call tools."));
            try
            {
                AssistantTurn final = await _provider.CompleteAsync(_messages, new JsonArray(), ct).ConfigureAwait(false);
                string text = (final.Text ?? "").Trim();
                if (text.Length > 0)
                {
                    AssistantText?.Invoke(text);
                    _messages.Add(new ChatMessage { Role = Role.Assistant, Content = text });
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to the notice */ }
            Notice?.Invoke("Done — the model tried to repeat finished calls; the repeats were skipped.");
        }

        // Canonical form of an arguments JSON (object keys sorted recursively, no whitespace) so the
        // same call is recognised even when the model reorders keys between rounds.
        private static string CanonicalJson(string json)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                return node == null ? "{}" : Canonicalize(node).ToJsonString();
            }
            catch { return (json ?? "").Trim(); }
        }

        private static JsonNode Canonicalize(JsonNode node)
        {
            if (node is JsonObject o)
            {
                var sorted = new JsonObject();
                foreach (var kv in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sorted[kv.Key] = kv.Value == null ? null : Canonicalize(kv.Value.DeepClone());
                return sorted;
            }
            if (node is JsonArray arr)
            {
                var copy = new JsonArray();
                foreach (JsonNode? item in arr)
                    copy.Add(item == null ? null : Canonicalize(item.DeepClone()));
                return copy;
            }
            return node.DeepClone();
        }

        // The instructions given to every model that drives Revit through AICon.
        public static string DefaultSystemPrompt() =>
            "You are AICon, an assistant connected to the user's live Autodesk Revit session through tools. " +
            "You read, analyse, and edit the Revit model the user has open, in plain language.\n" +
            "UNITS: all lengths and coordinates are in MILLIMETERS, angles in degrees, areas in m2, volumes in m3.\n" +
            "PERFORMANCE — CRITICAL: never make many individual tool calls for bulk work. Creating or modifying " +
            "more than ~3 things? Put ALL operations into ONE 'batch' call (e.g. 36 create_floor_plan + 36 create_sheet " +
            "+ 36 place_view_on_sheet = one batch). Repeated individual calls are far slower.\n" +
            "For logic batch can't express (loops with computed values, read-then-write), write ONE run_code script " +
            "(C# 5 executed inside Revit) instead.\n" +
            "Good first steps: get_project_info, then list_categories or list_levels. Use export_view_image to actually " +
            "see the model. Every edit is one undoable Revit transaction.\n" +
            "Always confirm with the user before deleting elements or running destructive code. When you have completed " +
            "the request, reply with a short plain-language summary of what you did — do not call more tools.";

        // Prompt used for SMALL LOCAL models (Ollama 7–8B). Deliberately different from
        // DefaultSystemPrompt: short (small models follow short prompts better), no mention of
        // batch/run_code (they are not offered locally — see Tools.BuildLocalToolList), and it
        // teaches by example, because that is what makes 8B models pick the right tool.
        public static string LocalSystemPrompt() =>
            "You are AICon, connected to the user's live Autodesk Revit session through TOOLS.\n" +
            "RULES:\n" +
            "1. To DO anything in Revit you MUST call a tool. Never answer with JSON text, code, or " +
            "a description of what you would do — actually call the tool.\n" +
            "2. Use exactly ONE tool call at a time, then wait for its result. NEVER repeat a call " +
            "that already succeeded — if a result says something was created, that step is DONE.\n" +
            "3. Pick the dedicated tool whose name matches the task. Examples:\n" +
            "   - create a sheet named A1  -> create_sheet {\"name\":\"A1\"}\n" +
            "   - how many levels?         -> list_levels {}\n" +
            "   - project name?            -> get_project_info {}\n" +
            "   - view template named T1   -> create_view_template {\"name\":\"T1\"}\n" +
            "   - in template T1 hide walls whose type name contains GLZ ->\n" +
            "     create_view_filter {\"name\":\"F1\",\"view_name\":\"T1\",\"categories\":[\"Walls\"]," +
            "\"parameter\":\"Type Name\",\"operator\":\"contains\",\"value\":\"GLZ\"}\n" +
            "4. Units: lengths/coordinates in MILLIMETERS, angles in degrees, areas in m2.\n" +
            "4b. Copy names, numbers and parameter names EXACTLY as the user wrote them — never invent " +
            "your own names (no 'My Filter'); if the user said QWEN FILTER, the name is QWEN FILTER.\n" +
            "5. If no tool fits, say so briefly. Do not invent tools or write code.\n" +
            "6. When the task is complete, reply with ONE short sentence describing what was done. " +
            "Do not do extra work the user did not ask for.\n" +
            "7. Reply in the user's language (Arabic in, Arabic out).";

        // A canned successful exchange used to seed local-model conversations: one request, one tool
        // call, one result, one short closing sentence. Small models imitate what they see far more
        // reliably than they follow written rules.
        public static List<ChatMessage> LocalSeedMessages()
        {
            return new List<ChatMessage>
            {
                ChatMessage.User("اعمل شيت وسميه : SEED-SHEET"),
                new ChatMessage
                {
                    Role = Role.Assistant,
                    Content = "",
                    ToolCalls = new List<ToolCall> { new ToolCall("seed_1", "create_sheet", "{\"name\":\"SEED-SHEET\"}") }
                },
                new ChatMessage
                {
                    Role = Role.Tool,
                    ToolCallId = "seed_1",
                    Name = "create_sheet",
                    Content = "{\"id\":900001,\"number\":\"A-100\",\"name\":\"SEED-SHEET\"}"
                },
                new ChatMessage { Role = Role.Assistant, Content = "تم إنشاء شيت باسم SEED-SHEET برقم A-100." }
            };
        }

        private static string Compact(string json)
        {
            json = json.Replace('\n', ' ').Replace('\r', ' ');
            return json.Length <= 120 ? json : json.Substring(0, 120) + "…";
        }

        private static string Preview(string result)
        {
            string oneLine = result.Replace('\n', ' ').Replace('\r', ' ');
            return oneLine.Length <= 200 ? oneLine : oneLine.Substring(0, 200) + "…";
        }
    }
}
