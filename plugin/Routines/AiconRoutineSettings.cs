using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICon.Routines
{
    /// <summary>
    /// The safety switch for routine code execution.
    ///
    /// Deliberately a SWITCH, not a sandbox. A real sandbox for the Revit API is not achievable in v1
    /// (a routine that can touch the model can already delete it), so pretending otherwise would be
    /// worse than being plain: composed routines — which can only call existing, reviewed AICon tools —
    /// always work, and arbitrary C# is off until the user turns it on.
    /// </summary>
    public sealed class AiconRoutineSettings
    {
        [JsonPropertyName("allowCodeExecution")] public bool AllowCodeExecution { get; set; }

        // The run_code tool (ToolsExtended.cs) is a SEPARATE switch from AllowCodeExecution above:
        // run_code is arbitrary, unreviewed C# an AI can run this turn with no save/review step first,
        // whereas a script Routine was at least written once and compiled before it became a button.
        // Defaults to TRUE — unlike AllowCodeExecution's safe-by-default OFF — because run_code has
        // always been unconditionally available; defaulting this new switch to off would silently break
        // the existing "escape hatch" workflow the whole product is built around. This only exists so an
        // owner can turn it OFF later (e.g. before handing AICon to a second, less-trusted user).
        [JsonPropertyName("allowRunCode")] public bool AllowRunCode { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AICon", "routines.json");

        public static AiconRoutineSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AiconRoutineSettings>(File.ReadAllText(FilePath), JsonOpts)
                           ?? new AiconRoutineSettings();
            }
            catch { /* hand-edited or corrupt → treat as OFF, the safe direction */ }
            return new AiconRoutineSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
        }
    }
}
