using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AICon.Routines
{
    internal sealed class RoutineRunResult
    {
        internal bool Success;
        internal int StepsRun;
        internal string Error;                                  // null when Success
        internal List<object> StepResults = new List<object>();
        internal object ScriptResult;                            // kind=script only
    }

    // Replays a composed routine: each step is an ordinary aicon: tool call, executed in order through
    // the same dispatcher the AI and the MCP bridge use — so a routine can never do anything the tools
    // can't, and it inherits all their validation for free.
    //
    // Atomicity follows AR400: everything runs inside ONE TransactionGroup which is Assimilate()d at the
    // end, so the whole routine is a single Ctrl+Z. A failing step rolls the entire routine back unless
    // that step is explicitly marked continueOnError.
    internal static class RoutineExecutor
    {
        internal static RoutineRunResult Run(UIApplication app, Routine routine, Dictionary<string, object> inputs)
        {
            var result = new RoutineRunResult();
            if (routine == null) { result.Error = "No routine supplied."; return result; }

            string invalid = routine.Validate();
            if (invalid != null) { result.Error = invalid; return result; }

            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                result.Error = "Open a project first — this routine works on the model.";
                return result;
            }
            Document doc = uidoc.Document;

            if (doc.IsReadOnly)
            {
                result.Error = "This document is read-only, so the routine cannot change anything.";
                return result;
            }

            if (routine.IsScript)
            {
                result.Error = "Script routines need code execution, which is handled elsewhere " +
                               "(see RoutineScriptHost). RoutineExecutor only replays composed routines.";
                return result;
            }

            Dictionary<string, object> bound = BindInputs(routine, inputs, out string inputProblem);
            if (inputProblem != null) { result.Error = inputProblem; return result; }

            var saved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            using (var group = new TransactionGroup(doc, "AICon routine: " + routine.Name))
            {
                group.Start();
                try
                {
                    for (int i = 0; i < routine.Steps.Count; i++)
                    {
                        RoutineStep step = routine.Steps[i];
                        Dictionary<string, object> args = ResolveArgs(step.Args, bound, saved);

                        try
                        {
                            // Dispatch handles its own transaction per mutating tool; the group above is
                            // what makes the whole routine one undo entry.
                            object stepResult = ToolDispatcher.Dispatch(app, step.Tool, args);
                            result.StepsRun++;
                            result.StepResults.Add(stepResult);
                            if (!string.IsNullOrWhiteSpace(step.SaveResultAs))
                                saved[step.SaveResultAs] = stepResult;
                        }
                        catch (Exception ex)
                        {
                            if (!step.ContinueOnError)
                            {
                                group.RollBack();
                                result.Error = "Step " + (i + 1) + " (" + step.Tool + ") failed: " + ex.Message +
                                               "  Nothing was changed — the whole routine was rolled back.";
                                return result;
                            }
                            result.StepResults.Add(new Dictionary<string, object>
                            {
                                { "step", i + 1 }, { "tool", step.Tool }, { "skipped_after_error", ex.Message }
                            });
                        }
                    }

                    group.Assimilate();     // one undo entry for the whole routine
                    result.Success = true;
                    return result;
                }
                catch (Exception ex)
                {
                    try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
                    result.Error = "Routine failed: " + ex.Message;
                    return result;
                }
            }
        }

        /// <summary>Applies defaults, checks required values are present, and normalises types.</summary>
        internal static Dictionary<string, object> BindInputs(Routine routine,
            Dictionary<string, object> supplied, out string problem)
        {
            problem = null;
            var bound = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (routine.Inputs == null) return bound;

            foreach (RoutineInput inp in routine.Inputs)
            {
                object value = null;
                bool given = supplied != null && supplied.TryGetValue(inp.Name, out value) && value != null
                             && !(value is string s && s.Length == 0);
                if (!given) value = inp.Default;

                if (value == null || (value is string str && str.Length == 0))
                {
                    if (inp.Required)
                    {
                        problem = "'" + inp.DisplayLabel + "' is required.";
                        return bound;
                    }
                    continue;
                }

                if (string.Equals(inp.Type, RoutineInputType.Enum, StringComparison.OrdinalIgnoreCase) &&
                    inp.Options != null && inp.Options.Count > 0)
                {
                    string chosen = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!inp.Options.Any(o => string.Equals(o, chosen, StringComparison.OrdinalIgnoreCase)))
                    {
                        problem = "'" + inp.DisplayLabel + "' must be one of: " + string.Join(", ", inp.Options) + ".";
                        return bound;
                    }
                }

                bound[inp.Name] = value;
            }
            return bound;
        }

        // ---- placeholder resolution -------------------------------------------------------------
        //
        // A step argument may be a literal, or a string containing {{input.x}} / {{steps.y.field}}.
        // When the WHOLE string is a single placeholder the resolved value keeps its real type (so an
        // id stays a number); when it is embedded in text it is substituted as text.

        private static Dictionary<string, object> ResolveArgs(Dictionary<string, object> args,
            Dictionary<string, object> inputs, Dictionary<string, object> saved)
        {
            var resolved = new Dictionary<string, object>();
            if (args == null) return resolved;
            foreach (KeyValuePair<string, object> kv in args)
                resolved[kv.Key] = ResolveValue(kv.Value, inputs, saved);
            return resolved;
        }

        private static object ResolveValue(object value, Dictionary<string, object> inputs,
            Dictionary<string, object> saved)
        {
            if (value is System.Text.Json.JsonElement je) value = FromJsonElement(je);

            if (value is string text)
            {
                string whole = WholePlaceholder(text);
                if (whole != null) return Lookup(whole, inputs, saved) ?? text;
                return SubstituteInText(text, inputs, saved);
            }

            if (value is List<object> list)
                return list.Select(v => ResolveValue(v, inputs, saved)).ToList();

            if (value is Dictionary<string, object> dict)
            {
                var copy = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in dict)
                    copy[kv.Key] = ResolveValue(kv.Value, inputs, saved);
                return copy;
            }

            return value;
        }

        /// <summary>Returns the inner path when the string is exactly "{{...}}", else null.</summary>
        private static string WholePlaceholder(string text)
        {
            string t = text.Trim();
            if (t.Length < 5 || !t.StartsWith("{{") || !t.EndsWith("}}")) return null;
            string inner = t.Substring(2, t.Length - 4).Trim();
            return inner.IndexOf("{{", StringComparison.Ordinal) >= 0 ? null : inner;
        }

        private static string SubstituteInText(string text, Dictionary<string, object> inputs,
            Dictionary<string, object> saved)
        {
            if (text.IndexOf("{{", StringComparison.Ordinal) < 0) return text;
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }

                sb.Append(text, i, open - i);
                string path = text.Substring(open + 2, close - open - 2).Trim();
                object v = Lookup(path, inputs, saved);
                sb.Append(v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture));
                i = close + 2;
            }
            return sb.ToString();
        }

        /// <summary>Resolves "input.name" or "steps.alias.field" (field may be dotted).</summary>
        private static object Lookup(string path, Dictionary<string, object> inputs,
            Dictionary<string, object> saved)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string[] parts = path.Split('.');

            if (parts.Length >= 2 && parts[0].Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                object v;
                return inputs.TryGetValue(parts[1], out v) ? v : null;
            }

            if (parts.Length >= 2 && parts[0].Equals("steps", StringComparison.OrdinalIgnoreCase))
            {
                object current;
                if (!saved.TryGetValue(parts[1], out current)) return null;
                for (int i = 2; i < parts.Length && current != null; i++)
                {
                    var asDict = current as Dictionary<string, object>;
                    if (asDict == null) return null;
                    object next;
                    current = asDict.TryGetValue(parts[i], out next) ? next : null;
                }
                return current;
            }

            return null;
        }

        private static object FromJsonElement(System.Text.Json.JsonElement je)
        {
            switch (je.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String: return je.GetString();
                case System.Text.Json.JsonValueKind.True: return true;
                case System.Text.Json.JsonValueKind.False: return false;
                case System.Text.Json.JsonValueKind.Number:
                    return je.TryGetInt64(out long l) ? (object)l : je.GetDouble();
                case System.Text.Json.JsonValueKind.Array:
                    return je.EnumerateArray().Select(x => FromJsonElement(x)).ToList();
                case System.Text.Json.JsonValueKind.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in je.EnumerateObject()) d[p.Name] = FromJsonElement(p.Value);
                    return d;
                default: return null;
            }
        }
    }
}
