using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AICon.Agent;

namespace AICon
{
    // Executes tools for the in-Revit chat panel WITHOUT any HTTP hop: it enqueues a job onto the
    // very same ExternalEvent queue the localhost bridge uses, so tool code runs on Revit's UI thread
    // inside a transaction exactly as it does for Claude. This is the in-process twin of the console's
    // HttpToolExecutor.
    //
    // It also carries the panel's safety features:
    //   * read-only mode  — mutating tools are refused while the panel's toggle is on
    //   * delete confirm  — delete_elements asks the user first (via the panel-supplied callback)
    //   * undo tracking   — ids of elements created this turn, so "undo last" can delete them
    internal sealed class InProcessToolExecutor : IToolExecutor
    {
        private readonly Func<bool> _readOnly;                    // null = feature off
        private readonly Func<string, string, bool> _confirm;     // (title, message) -> allowed; null = no gate

        private readonly object _createdLock = new object();
        private readonly List<int> _createdIds = new List<int>();

        public InProcessToolExecutor(Func<bool> readOnly = null, Func<string, string, bool> confirm = null)
        {
            _readOnly = readOnly;
            _confirm = confirm;
        }

        /// <summary>Forget the created-element ids of the previous turn (call when a new turn starts).</summary>
        public void BeginTurn() { lock (_createdLock) _createdIds.Clear(); }

        /// <summary>Ids of elements created since BeginTurn — the "undo last" candidates.</summary>
        public List<int> CreatedIds { get { lock (_createdLock) return new List<int>(_createdIds); } }

        public Task<string> ExecuteAsync(string tool, string argumentsJson, CancellationToken ct)
        {
            // The agent loop already runs off the UI thread; do the blocking wait on a worker so the
            // async signature is honoured and cancellation works.
            return Task.Run(() => ExecuteBlocking(tool, argumentsJson, ct, false), ct);
        }

        /// <summary>Run a tool on behalf of the USER (e.g. the undo button): the read-only and
        /// delete-confirmation gates are for MODEL-initiated calls, so they are bypassed here.</summary>
        public Task<string> ExecuteDirect(string tool, string argumentsJson)
        {
            return Task.Run(() => ExecuteBlocking(tool, argumentsJson, CancellationToken.None, true));
        }

        private string ExecuteBlocking(string tool, string argumentsJson, CancellationToken ct, bool skipGates)
        {
            if (App.Handler == null || App.BridgeEvent == null)
                return "AICon bridge is not initialised. Restart Revit and reopen the AICon chat.";

            if (!skipGates && _readOnly != null && _readOnly() && ToolDispatcher.IsMutating(tool))
                return "BLOCKED: read-only mode is ON in the AICon panel, so '" + tool + "' was not run. " +
                       "Tell the user to untick 'Read-only' to allow model changes.";

            Dictionary<string, object> args;
            try
            {
                args = string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}"
                    ? new Dictionary<string, object>()
                    : Json.DeserializeObject(argumentsJson) ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                return "Error: could not parse arguments as JSON: " + ex.Message;
            }

            if (!skipGates && tool == "delete_elements" && _confirm != null)
            {
                int count = args.ContainsKey("ids") && args["ids"] is List<object> l ? l.Count : 0;
                string what = count > 0 ? count + " element(s)" : "elements";
                if (!_confirm("AICon — delete?", "The AI wants to DELETE " + what + " from the model.\n\nAllow it?"))
                    return "The user DECLINED the deletion — the elements were NOT deleted. Do not retry; " +
                           "ask the user what they want instead.";
            }

            var job = new BridgeJob { Tool = tool, Args = args };
            App.Handler.Queue.Enqueue(job);
            App.BridgeEvent.Raise();

            // The external event only fires when Revit is idle (no modal dialog / active edit mode).
            if (!job.Done.Wait(TimeSpan.FromSeconds(110), ct))
                return "Revit did not respond in time. It may be busy, showing a dialog, or in an active " +
                       "edit mode. Finish what Revit is doing and try again.";

            // job.ResultJson is the same { ok, data, error } envelope the HTTP bridge returns; reuse the
            // shared interpreter so the model sees identical results on both paths.
            string text = BridgeResult.ToText(job.ResultJson);
            TrackCreatedIds(tool, text);
            return text;
        }

        // Remember ids of things a create-style tool produced, for the panel's "undo last" button.
        private void TrackCreatedIds(string tool, string resultText)
        {
            if (!(tool.StartsWith("create_", StringComparison.Ordinal) ||
                  tool == "duplicate_view" || tool == "place_family_instance" || tool == "place_view_on_sheet" ||
                  tool == "copy_elements" || tool == "mirror_elements"))
                return;
            try
            {
                JsonNode node = JsonNode.Parse(resultText);
                var obj = node as JsonObject;
                if (obj == null) return;
                foreach (string key in new[] { "id", "filter_id", "viewport_id", "marker_id" })
                    if (obj[key] is JsonValue v && v.TryGetValue(out int id))
                        lock (_createdLock) _createdIds.Add(id);
                if (obj["ids"] is JsonArray arr)
                    foreach (JsonNode n in arr)
                        if (n is JsonValue av && av.TryGetValue(out int id2))
                            lock (_createdLock) _createdIds.Add(id2);
            }
            catch { /* result was not JSON — nothing to track */ }
        }
    }
}
