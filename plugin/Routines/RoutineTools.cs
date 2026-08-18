using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace AICon
{
    using AICon.Routines;

    // The routine tools the AI sees over MCP. Registered in ToolDispatcher like every other tool, so
    // they inherit the same transport, transaction handling and error reporting.
    internal static partial class ToolDispatcher
    {
        internal static object ListRoutines(Dictionary<string, object> args)
        {
            List<string> problems;
            List<Routine> routines = RoutineStore.LoadAll(out problems);

            var list = routines.Select(r => (object)new Dictionary<string, object>
            {
                { "id", r.Id },
                { "name", r.Name },
                { "description", r.Description },
                { "kind", r.Kind },
                { "read_only", r.ReadOnly },
                { "destructive", r.Destructive },
                { "inputs", (r.Inputs ?? new List<RoutineInput>()).Select(i => (object)new Dictionary<string, object>
                    {
                        { "name", i.Name },
                        { "type", i.Type },
                        { "required", i.Required },
                        { "description", i.Description },
                        { "options", i.Options }
                    }).ToList() },
                { "folder", r.FolderPath }
            }).ToList();

            var result = new Dictionary<string, object>
            {
                { "count", list.Count },
                { "routines", list },
                { "user_root", RoutineStore.UserRoot },
                { "code_execution_enabled", AiconRoutineSettings.Load().AllowCodeExecution }
            };
            if (problems != null && problems.Count > 0) result["skipped"] = problems.Take(10).Cast<object>().ToList();
            return result;
        }

        internal static object SaveRoutine(Dictionary<string, object> args)
        {
            string json = Json.GetString(args, "routine_json");
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(
                    "'routine_json' is required — the full routine.json content as a JSON string. " +
                    "Call get_authoring_guide first if you are unsure of the format.");

            Routine routine;
            try
            {
                routine = System.Text.Json.JsonSerializer.Deserialize<Routine>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("'routine_json' is not valid JSON: " + ex.Message);
            }
            if (routine == null) throw new InvalidOperationException("'routine_json' parsed to nothing.");

            string invalid = routine.Validate();
            if (invalid != null) throw new InvalidOperationException("The routine is not valid: " + invalid);

            // Script sources arrive as a { "filename.cs": "source" } map and are written next to
            // routine.json — never embedded in the JSON itself.
            var extra = new Dictionary<string, string>();
            Dictionary<string, object> files = Json.GetDict(args, "files");
            if (files != null)
                foreach (KeyValuePair<string, object> f in files)
                    extra[f.Key] = Convert.ToString(f.Value);

            if (routine.IsScript)
            {
                foreach (string needed in routine.Script.Files)
                    if (!extra.ContainsKey(Path.GetFileName(needed)))
                        throw new InvalidOperationException(
                            "The routine lists script file '" + needed + "' but 'files' does not contain it.");

                // Compile BEFORE saving. Saving a routine that cannot compile just creates a broken
                // button, and the diagnostics are far more useful to the agent right now.
                foreach (KeyValuePair<string, string> f in extra)
                {
                    ScriptCompileResult check = AiconScriptCompiler.CompileSnippet(f.Value, "AiconRoutineCheck");
                    if (!check.Success)
                        return new Dictionary<string, object>
                        {
                            { "saved", false },
                            { "reason", "compile_failed" },
                            { "file", f.Key },
                            { "errors", check.Errors.Select(e => (object)new Dictionary<string, object>
                                {
                                    { "id", e.Id }, { "message", e.Message },
                                    { "line", e.Line }, { "column", e.Column }, { "file", e.File }
                                }).ToList() },
                            { "note", "Nothing was saved. Line numbers refer to the source you sent." }
                        };
                }
            }

            string folder = RoutineStore.Save(routine, extra, Json.GetString(args, "target_root"));

            return new Dictionary<string, object>
            {
                { "saved", true },
                { "id", routine.Id },
                { "name", routine.Name },
                { "kind", routine.Kind },
                { "folder", folder },
                { "note", "Runnable now from the AICon ribbon → Routines. " +
                          (routine.Ribbon != null
                              ? "It gets its own ribbon button after Revit restarts (Revit only creates buttons at startup)."
                              : "It has no ribbon block, so it lives in the Routines list only.") }
            };
        }

        internal static object RunRoutine(Autodesk.Revit.UI.UIApplication app, Dictionary<string, object> args)
        {
            string id = Json.GetString(args, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("'id' is required (see list_routines).");

            Routine routine = RoutineStore.FindById(id);
            if (routine == null) throw new InvalidOperationException("No routine with id '" + id + "'. Use list_routines.");

            var inputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> supplied = Json.GetDict(args, "inputs");
            if (supplied != null) foreach (KeyValuePair<string, object> kv in supplied) inputs[kv.Key] = kv.Value;

            RoutineRunResult run = routine.IsScript
                ? RoutineScriptHost.Run(app, routine, inputs)
                : RoutineExecutor.Run(app, routine, inputs);

            var result = new Dictionary<string, object>
            {
                { "ok", run.Success },
                { "routine", routine.Name },
                { "steps_run", run.StepsRun }
            };
            if (!run.Success) result["error"] = run.Error;
            if (run.ScriptResult != null) result["result"] = run.ScriptResult;
            if (run.StepResults != null && run.StepResults.Count > 0) result["step_results"] = run.StepResults;
            return result;
        }

        internal static object GetAuthoringGuide(Dictionary<string, object> args)
        {
            string text = LoadAuthoringGuide();
            return new Dictionary<string, object>
            {
                { "format", "markdown" },
                { "length_chars", text.Length },
                { "guide", text }
            };
        }

        // The guide ships next to the DLL so it can be corrected without a rebuild; the embedded copy
        // is the fallback so the tool never returns nothing.
        private static string LoadAuthoringGuide()
        {
            try
            {
                string beside = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "AUTHORING.md");
                if (File.Exists(beside)) return File.ReadAllText(beside);
            }
            catch { }

            return "AUTHORING.md was not found next to AICon.dll.\n\n" +
                   "Routine format in brief: a folder containing routine.json with schemaVersion 1, an id " +
                   "(lowercase-kebab), name, and kind = \"composed\" (a steps[] list of aicon: tool calls, " +
                   "arguments may use {{input.x}} and {{steps.alias.field}}) or \"script\" (script.files[] " +
                   "listing sibling .cs files). Declare inputs[] with name/type " +
                   "(string|number|integer|boolean|enum|stringArray|elementId) — AICon builds both the user " +
                   "form and this tool's schema from it. Save with save_routine, run with run_routine.";
        }
    }
}
