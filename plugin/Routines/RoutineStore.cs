using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AICon.Routines
{
    // Finds, loads and saves routines. A routine is a FOLDER (routine.json + optional .cs + icon), so
    // the whole thing can be dropped on a shared drive and picked up by every machine that has AICon.
    //
    // Two roots are scanned, in this order:
    //   1. the user's own    %APPDATA%\AICon\routines
    //   2. an optional team folder, from %APPDATA%\AICon\routine-roots.txt (one path per line)
    // A routine in a later root does NOT override an earlier one — the first id wins, so a user's own
    // copy always beats the team copy and nobody's button changes under them without warning.
    internal static class RoutineStore
    {
        private const string RoutineFileName = "routine.json";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        internal static string UserRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "routines");

        private static string RootsListFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "routine-roots.txt");

        /// <summary>Every folder scanned for routines: the user's own first, then any team folders.</summary>
        internal static List<string> Roots()
        {
            var roots = new List<string> { UserRoot };
            try
            {
                if (File.Exists(RootsListFile))
                    foreach (string raw in File.ReadAllLines(RootsListFile))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        if (!roots.Any(r => string.Equals(r, line, StringComparison.OrdinalIgnoreCase)))
                            roots.Add(line);
                    }
            }
            catch (Exception ex) { App.Log("Routines: could not read routine-roots.txt: " + ex.Message); }
            return roots;
        }

        /// <summary>Loads every valid routine. A broken routine never stops the others from loading —
        /// it is logged and skipped, because one bad file on a team share must not remove everyone's buttons.</summary>
        internal static List<Routine> LoadAll(out List<string> problems)
        {
            problems = new List<string>();
            var byId = new Dictionary<string, Routine>(StringComparer.OrdinalIgnoreCase);

            foreach (string root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                string[] folders;
                try { folders = Directory.GetDirectories(root); }
                catch (Exception ex) { problems.Add(root + ": " + ex.Message); continue; }

                foreach (string folder in folders)
                {
                    string file = Path.Combine(folder, RoutineFileName);
                    if (!File.Exists(file)) continue;
                    try
                    {
                        Routine r = Load(file);
                        string invalid = r.Validate();
                        if (invalid != null) { problems.Add(Path.GetFileName(folder) + ": " + invalid); continue; }
                        if (!byId.ContainsKey(r.Id)) byId[r.Id] = r;   // first root wins
                    }
                    catch (Exception ex) { problems.Add(Path.GetFileName(folder) + ": " + ex.Message); }
                }
            }

            return byId.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static Routine Load(string routineJsonPath)
        {
            string json = File.ReadAllText(routineJsonPath);
            Routine r = JsonSerializer.Deserialize<Routine>(json, JsonOpts);
            if (r == null) throw new InvalidOperationException("routine.json is empty or not an object.");
            r.FolderPath = Path.GetDirectoryName(routineJsonPath);
            return r;
        }

        /// <summary>
        /// Writes a routine to its own folder, named after its id. Saving the same id twice UPDATES in
        /// place — no orphan folders, no "routine (2)". Optional extra files (C# sources) are written
        /// alongside. Returns the folder path.
        /// </summary>
        internal static string Save(Routine routine, IDictionary<string, string> extraFiles, string targetRoot)
        {
            string invalid = routine.Validate();
            if (invalid != null) throw new InvalidOperationException("Cannot save this routine: " + invalid);

            string root = string.IsNullOrWhiteSpace(targetRoot) ? UserRoot : targetRoot;
            string folder = Path.Combine(root, routine.Id);
            Directory.CreateDirectory(folder);

            string nowIso = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string existing = Path.Combine(folder, RoutineFileName);
            if (File.Exists(existing) && string.IsNullOrWhiteSpace(routine.Created))
            {
                // Preserve the original creation date across an update.
                try { routine.Created = Load(existing).Created; } catch { }
            }
            if (string.IsNullOrWhiteSpace(routine.Created)) routine.Created = nowIso;
            routine.Modified = nowIso;

            if (extraFiles != null)
                foreach (KeyValuePair<string, string> f in extraFiles)
                {
                    // Guard against a routine writing outside its own folder.
                    string name = Path.GetFileName(f.Key);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    File.WriteAllText(Path.Combine(folder, name), f.Value ?? "", new UTF8Encoding(false));
                }

            File.WriteAllText(existing, JsonSerializer.Serialize(routine, JsonOpts), new UTF8Encoding(false));
            routine.FolderPath = folder;
            return folder;
        }

        internal static Routine FindById(string id)
        {
            List<string> ignored;
            return LoadAll(out ignored)
                .FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
