using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
    /// Compiles (once per session) and runs script routines. Compiled assemblies are cached by routine
    /// id — .NET Framework cannot unload them anyway, so re-compiling the same routine repeatedly would
    /// only leak more.
    /// </summary>
    internal static class RoutineScriptHost
    {
        private static readonly Dictionary<string, Type> Cache =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

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

            Type routineType;
            if (!Cache.TryGetValue(routine.Id, out routineType))
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
                Cache[routine.Id] = routineType;
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
