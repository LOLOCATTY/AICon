using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AICon.Routines
{
    /// <summary>The contract a script routine implements. Bare-body routines get this generated for them.</summary>
    public interface IAiconRoutine
    {
        object Run(UIApplication uiapp, UIDocument uidoc, Document doc, RoutineInputs input);
    }

    /// <summary>
    /// The routine's inputs, with forgiving accessors. A routine should never crash because a number
    /// arrived as a string — that is the host's problem to absorb, not the author's.
    /// </summary>
    public sealed class RoutineInputs
    {
        private readonly Dictionary<string, object> _values;

        public RoutineInputs(Dictionary<string, object> values)
        {
            _values = values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public bool Has(string name) => _values.ContainsKey(name);

        public object Raw(string name)
        {
            object v;
            return _values.TryGetValue(name, out v) ? v : null;
        }

        public string Text(string name, string fallback = null)
        {
            object v = Raw(name);
            return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public double Number(string name, double fallback = 0)
        {
            object v = Raw(name);
            if (v == null) return fallback;
            if (v is double d) return d;
            if (v is int i) return i;
            if (v is long l) return l;
            double parsed;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        public int Int(string name, int fallback = 0) => (int)Math.Round(Number(name, fallback));

        public bool Bool(string name, bool fallback = false)
        {
            object v = Raw(name);
            if (v == null) return fallback;
            if (v is bool b) return b;
            bool parsed;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public List<string> TextList(string name)
        {
            var result = new List<string>();
            object v = Raw(name);
            if (v == null) return result;
            var seq = v as System.Collections.IEnumerable;
            if (seq != null && !(v is string))
                foreach (object o in seq) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            else
                result.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
            return result;
        }

        /// <summary>An 'elementId' input as a real ElementId, or ElementId.InvalidElementId.</summary>
        public ElementId Element(string name)
        {
            double n = Number(name, -1);
            return n <= 0 ? ElementId.InvalidElementId : AICon.ElementIdCompat.FromInt((int)n);
        }
    }

    /// <summary>
    /// Compiles and runs script routines, caching the compiled Type by routine id so a routine that
    /// hasn't changed doesn't get recompiled on every click. .NET Framework can never unload an
    /// assembly once loaded, so the cache also carries a hash of the routine's own source: a hit only
    /// reuses the cached Type when the source on disk still matches what produced it. save_routine
    /// overwriting the .cs file with new content changes the hash, so the very next run_routine call
    /// compiles the new code fresh (into a NEW assembly — the OLD one stays resident in memory, since
    /// it still can't be unloaded, but it is simply never used again). This is what makes editing an
    /// existing routine take effect without restarting Revit; only a brand-new routine's own RIBBON
    /// BUTTON still needs a restart to appear, because Revit only builds ribbon panels at startup —
    /// that part is a genuinely separate Revit API limit, unrelated to this cache.
    /// </summary>
    internal static class RoutineScriptHost
    {
        private sealed class CachedRoutine
        {
            internal Type Type;
            internal string SourceHash;
        }

        private static readonly Dictionary<string, CachedRoutine> Cache =
            new Dictionary<string, CachedRoutine>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Hashes the routine's own script files (name, length and content, in declared order) so the
        /// cache can tell "same code" from "edited since last run". This only has to catch "did the
        /// source change", not resist a deliberately adversarial collision, so plain framing is enough.
        /// Returns null on any read failure — the caller then always falls through to a real compile,
        /// whose own error path (missing file, etc.) is the correct thing to surface, not a silently
        /// stale cache hit.
        /// </summary>
        private static string ComputeSourceHash(Routine routine)
        {
            if (routine.Script == null || routine.Script.Files == null || routine.Script.Files.Count == 0)
                return null;
            try
            {
                var sb = new StringBuilder();
                foreach (string name in routine.Script.Files)
                {
                    string path = Path.Combine(routine.FolderPath ?? "", Path.GetFileName(name));
                    string content = File.ReadAllText(path);
                    sb.Append(Path.GetFileName(path)).Append(':').Append(content.Length).Append('\n')
                      .Append(content).Append('\n');
                }
                using (MD5 md5 = MD5.Create())
                    return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            }
            catch { return null; }
        }

        internal static RoutineRunResult Run(UIApplication app, Routine routine, Dictionary<string, object> inputs)
        {
            var result = new RoutineRunResult();

            string settingsError;
            AiconRoutineSettings settings = AiconRoutineSettings.Load(out settingsError);
            if (!settings.AllowCodeExecution)
            {
                // Re-read fresh every call (no restart needed for this switch to take effect) — so if
                // it's still off after the user says they set it, the file itself is the thing to check.
                result.Error = "Script routines are turned off. Set \"allowCodeExecution\": true in " +
                               AiconRoutineSettings.FilePath + " (no restart needed — this is checked " +
                               "fresh every run).";
                if (settingsError != null)
                    result.Error += " NOTE: that file exists but could not be read as JSON (" +
                                     settingsError + ") — it is being treated as OFF because of that, " +
                                     "not because the key is missing. Fix or delete the file and try again.";
                return result;
            }

            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                result.Error = "Open a project first.";
                return result;
            }

            string inputProblem;
            Dictionary<string, object> bound = RoutineExecutor.BindInputs(routine, inputs, out inputProblem);
            if (inputProblem != null) { result.Error = inputProblem; return result; }

            // Cache hit only when the routine's own source still matches what produced the cached Type —
            // see ComputeSourceHash and the class doc comment above. currentHash is computed even on
            // what will be a cache hit; it's a couple of tiny file reads plus an MD5 of a few KB, cheap
            // next to actually compiling.
            string currentHash = ComputeSourceHash(routine);

            Type routineType;
            CachedRoutine cached;
            bool haveCachedHit = Cache.TryGetValue(routine.Id, out cached)
                                 && currentHash != null && cached.SourceHash == currentHash;
            if (haveCachedHit)
            {
                routineType = cached.Type;
            }
            else
            {
                ScriptCompileResult compiled = AiconScriptCompiler.CompileRoutine(routine);
                if (!compiled.Success)
                {
                    result.Error = "The routine's code did not compile:\n" +
                                   string.Join("\n", compiled.Errors.Select(e => e.ToString()).Take(10));
                    return result;
                }

                routineType = compiled.Assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IAiconRoutine).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (routineType == null)
                {
                    result.Error = "The code compiled but contains no class implementing IAiconRoutine.";
                    return result;
                }
                Cache[routine.Id] = new CachedRoutine { Type = routineType, SourceHash = currentHash };
            }

            Document doc = uidoc.Document;
            using (var group = new TransactionGroup(doc, "AICon routine: " + routine.Name))
            {
                group.Start();
                try
                {
                    var instance = (IAiconRoutine)Activator.CreateInstance(routineType);
                    // A read-only routine gets no transaction of its own — it should not need one, and
                    // if it tries to write, Revit's own error is clearer than anything invented here.
                    if (routine.ReadOnly)
                    {
                        result.ScriptResult = instance.Run(app, uidoc, doc, new RoutineInputs(bound));
                    }
                    else
                    {
                        using (var tx = new Transaction(doc, routine.Name))
                        {
                            tx.Start();
                            result.ScriptResult = instance.Run(app, uidoc, doc, new RoutineInputs(bound));
                            tx.Commit();
                        }
                    }

                    group.Assimilate();
                    result.Success = true;
                    result.StepsRun = 1;
                    return result;
                }
                catch (Exception ex)
                {
                    try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
                    Exception real = ex.InnerException ?? ex;   // Activator/Invoke wrap the real one
                    result.Error = "The routine threw: " + real.Message;
                    App.Log("Routine '" + routine.Id + "' threw: " + real);
                    return result;
                }
            }
        }
    }
}
