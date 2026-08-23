using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AICon.Routines
{
    // The in-memory shape of routine.json. Kept deliberately free of Revit API types so it can be
    // read, written and validated without a document open (and unit-tested outside Revit).
    // See schemas/routine.schema.json — that file is the contract, this is its C# mirror.
    public sealed class Routine
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }

        /// <summary>"composed" (a list of aicon: tool calls) or "script" (C# in sibling .cs files).</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = RoutineKind.Composed;

        [JsonPropertyName("ribbon")] public RoutineRibbon Ribbon { get; set; }
        [JsonPropertyName("inputs")] public List<RoutineInput> Inputs { get; set; } = new List<RoutineInput>();

        [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
        [JsonPropertyName("destructive")] public bool Destructive { get; set; }

        [JsonPropertyName("author")] public string Author { get; set; }
        [JsonPropertyName("created")] public string Created { get; set; }
        [JsonPropertyName("modified")] public string Modified { get; set; }

        [JsonPropertyName("steps")] public List<RoutineStep> Steps { get; set; }
        [JsonPropertyName("script")] public RoutineScript Script { get; set; }

        /// <summary>Absolute folder this routine was loaded from. Not serialised — it is where the
        /// routine lives, not part of what it is, and hard-coding it would break portability.</summary>
        [JsonIgnore] public string FolderPath { get; set; }

        [JsonIgnore] public bool IsComposed =>
            string.Equals(Kind, RoutineKind.Composed, StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsScript =>
            string.Equals(Kind, RoutineKind.Script, StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns null when the routine is usable, otherwise why it is not. Checked at load
        /// AND at save, so a hand-edited file fails with a sentence instead of a null reference later.</summary>
        public string Validate()
        {
            if (SchemaVersion != CurrentSchemaVersion)
                return "schemaVersion " + SchemaVersion + " is not supported by this build of AICon (expected " +
                       CurrentSchemaVersion + "). Refusing to guess what it means.";
            if (string.IsNullOrWhiteSpace(Id)) return "'id' is required.";
            if (!IsValidId(Id))
                return "'id' must be lowercase letters, digits and hyphens (2-64 chars) — it is used as the folder name.";
            if (string.IsNullOrWhiteSpace(Name)) return "'name' is required.";

            if (IsComposed)
            {
                if (Steps == null || Steps.Count == 0) return "a composed routine needs at least one step.";
                for (int i = 0; i < Steps.Count; i++)
                    if (Steps[i] == null || string.IsNullOrWhiteSpace(Steps[i].Tool))
                        return "step " + (i + 1) + " has no 'tool'.";
            }
            else if (IsScript)
            {
                if (Script == null || Script.Files == null || Script.Files.Count == 0)
                    return "a script routine needs 'script.files' listing at least one .cs file.";
            }
            else return "'kind' must be either \"composed\" or \"script\".";

            if (Inputs != null)
                foreach (RoutineInput inp in Inputs)
                {
                    string problem = inp == null ? "an input entry is empty." : inp.Validate();
                    if (problem != null) return problem;
                }
            return null;
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2 || id.Length > 64) return false;
            if (!IsIdChar(id[0]) || id[0] == '-') return false;
            foreach (char c in id)
                if (!IsIdChar(c)) return false;
            return true;
        }

        private static bool IsIdChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
    }

    public static class RoutineKind
    {
        public const string Composed = "composed";
        public const string Script = "script";
    }

    public sealed class RoutineRibbon
    {
        [JsonPropertyName("panel")] public string Panel { get; set; } = "Routines";
        [JsonPropertyName("buttonText")] public string ButtonText { get; set; }
        [JsonPropertyName("tooltip")] public string Tooltip { get; set; }
        [JsonPropertyName("icon")] public string Icon { get; set; }
    }

    public static class RoutineInputType
    {
        public const string String = "string";
        public const string Number = "number";
        public const string Integer = "integer";
        public const string Boolean = "boolean";
        public const string Enum = "enum";
        public const string StringArray = "stringArray";
        public const string ElementId = "elementId";
    }

    public sealed class RoutineInput
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = RoutineInputType.String;
        [JsonPropertyName("options")] public List<string> Options { get; set; }
        // Enum-only, both optional, additive (existing routines with 'options' and no 'source'/'multi'
        // behave exactly as before):
        //  - 'source': read the choices LIVE from the open model when the form is built, instead of the
        //    fixed 'options' list baked into routine.json at author time. See RoutineInputSource for the
        //    recognised values. 'options' is still allowed alongside 'source' as a documentation-only
        //    fallback shape; the live list always wins when both are present.
        //  - 'multi': true turns a single ComboBox into a checkbox list and the bound value into a
        //    List<object> of the checked strings, instead of one string.
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("multi")] public bool Multi { get; set; }
        [JsonPropertyName("required")] public bool Required { get; set; } = true;
        [JsonPropertyName("default")] public object Default { get; set; }

        [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "an input has no 'name'.";
            switch ((Type ?? "").Trim())
            {
                case RoutineInputType.String:
                case RoutineInputType.Number:
                case RoutineInputType.Integer:
                case RoutineInputType.Boolean:
                case RoutineInputType.StringArray:
                case RoutineInputType.ElementId:
                    return null;
                case RoutineInputType.Enum:
                    if (!string.IsNullOrWhiteSpace(Source))
                        return RoutineInputSource.IsKnown(Source)
                            ? null
                            : "input '" + Name + "' has unknown source '" + Source + "'. Known sources: " +
                              RoutineInputSource.KnownList + ".";
                    return (Options != null && Options.Count > 0)
                        ? null
                        : "input '" + Name + "' is an enum but has no 'options' (or a 'source' to read choices live from the model).";
                default:
                    return "input '" + Name + "' has unknown type '" + Type + "'.";
            }
        }
    }

    /// <summary>
    /// Known live-model sources an enum input's choices can be read from when the routine's form opens,
    /// instead of (or alongside, as a documentation fallback for) a fixed 'options' list. Deliberately a
    /// small, curated set — reusing the same categories the read tools already expose (list_levels,
    /// list_views, ...) — rather than letting a routine author name an arbitrary Revit query.
    /// </summary>
    public static class RoutineInputSource
    {
        public const string Levels = "levels";
        public const string Views = "views";
        public const string Sheets = "sheets";
        public const string Categories = "categories";
        public const string Worksets = "worksets";

        private static readonly string[] All = { Levels, Views, Sheets, Categories, Worksets };

        public static bool IsKnown(string source) =>
            All.Any(s => string.Equals(s, (source ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        public static string KnownList => string.Join(", ", All);
    }

    public sealed class RoutineStep
    {
        [JsonPropertyName("tool")] public string Tool { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; }
        [JsonPropertyName("saveResultAs")] public string SaveResultAs { get; set; }
        [JsonPropertyName("continueOnError")] public bool ContinueOnError { get; set; }
        [JsonPropertyName("comment")] public string Comment { get; set; }
    }

    public sealed class RoutineScript
    {
        [JsonPropertyName("files")] public List<string> Files { get; set; } = new List<string>();
        [JsonPropertyName("entry")] public string Entry { get; set; }
    }
}
