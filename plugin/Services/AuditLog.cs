using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AICon
{
    /// <summary>
    /// The Log layer: a structured, append-only audit trail of every mutating and Unsandboxed
    /// (run_code) tool execution, written unconditionally regardless of which front door triggered it.
    ///
    /// Deliberately separate from the two logs that already existed: bridge.log (App.Log) is free-text
    /// diagnostics for startup/errors, and decisions.log (AIConDecisionClient.Log) is scoped entirely to
    /// the AI-decision layer (level-mapping, tag-position, ...). Neither records what a tool call
    /// actually did to the model. This one JSON-line file is the answer to "if a routine ran wrong,
    /// what exactly changed" — that only works if every mutating call is logged the same way, in one
    /// place, whether it came from Claude Desktop, the in-Revit panel, or a Routine.
    ///
    /// "who" ran something is answered honestly, not aspirationally: AICon has no user/session identity
    /// anywhere (single machine, no login), so 'frontDoor' — "mcp" / "in_revit_panel" / "routine" — is
    /// the closest true answer, never a real per-person identity.
    /// </summary>
    internal static class AuditLog
    {
        private static readonly string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "audit.log");

        /// <summary>
        /// One entry per resolved mutating operation (NOT one per InTransaction call — a batch of 40
        /// ops writes 40 lines, one per op, so a partially-failed continue_on_error batch shows exactly
        /// which ones actually stuck). 'batchLabel' is set only for a line originating inside a batch.
        /// </summary>
        internal static void Write(string tool, ToolDispatcher.ToolTier tier, string frontDoor, string summary,
            List<object> elementIds, bool success, string error, string batchLabel = null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                var entry = new Dictionary<string, object>
                {
                    ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["tool"] = tool,
                    ["tier"] = tier.ToString(),
                    ["front_door"] = frontDoor ?? "unknown",
                    ["success"] = success
                };
                if (batchLabel != null) entry["batch_op"] = batchLabel;
                if (summary != null) entry["summary"] = summary;
                // Capped — a delete on a 5,000-element selection should not balloon one log line.
                if (elementIds != null && elementIds.Count > 0)
                    entry["element_ids"] = elementIds.Take(200).ToList();
                if (error != null) entry["error"] = Truncate(error, 500);
                File.AppendAllText(LogPath, Json.Serialize(entry) + Environment.NewLine);
            }
            catch { /* logging must never break a tool call */ }
        }

        private static string Truncate(string s, int max) =>
            s != null && s.Length > max ? s.Substring(0, max) + "…" : s;
    }
}
