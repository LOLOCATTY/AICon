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

        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AICon", "routines.json");

        public static AiconRoutineSettings Load()
        {
            string ignored;
            return Load(out ignored);
        }

        /// <summary>
        /// Same as <see cref="Load()"/>, but on a hand-edited-and-broken file <paramref name="parseError"/>
        /// carries WHY it fell back to OFF instead of leaving the user to guess. A real report: a user
        /// added <c>{"allowCodeExecution": true}</c> by hand, restarted Revit, and still got "turned
        /// off" with no clue their file wasn't actually being read (a stray smart-quote from pasting out
        /// of a rich-text editor is enough to fail JSON.Parse silently). This still defaults to OFF on
        /// any failure — the safe direction — it just stops being silent about it.
        /// </summary>
        public static AiconRoutineSettings Load(out string parseError)
        {
            parseError = null;
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AiconRoutineSettings>(File.ReadAllText(FilePath), JsonOpts)
                           ?? new AiconRoutineSettings();
            }
            catch (Exception ex) { parseError = ex.Message; }
            return new AiconRoutineSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
        }
    }
}
