using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace AICon.Routines
{
    internal sealed class ScriptDiagnostic
    {
        internal string Id;         // e.g. CS0103
        internal string Message;
        internal string File;
        internal int Line;          // 1-based, in the AUTHOR'S source (see the #line trick below)
        internal int Column;

        public override string ToString() =>
            (File ?? "script.cs") + "(" + Line + "," + Column + "): " + Id + ": " + Message;
    }

    internal sealed class ScriptCompileResult
    {
        internal Assembly Assembly;
        internal List<ScriptDiagnostic> Errors = new List<ScriptDiagnostic>();
        internal bool Success => Assembly != null;
    }

    /// <summary>
    /// Compiles routine C# in memory so a saved routine becomes a working button without restarting
    /// Revit and without a DLL on disk.
    ///
    /// Two source shapes are accepted:
    ///   • a FULL class implementing IAiconRoutine — used as-is;
    ///   • a BARE BODY (statements only) — wrapped automatically with uiapp/uidoc/doc/input already in
    ///     scope, so a five-line routine is five lines.
    ///
    /// PLATFORM NOTE (.NET Framework 4.8): the compiled assembly is loaded with Assembly.Load(byte[])
    /// into the current AppDomain, which .NET Framework can never unload. Consequence: saving a NEW
    /// routine works immediately, but EDITING an already-loaded routine needs a Revit restart to pick
    /// up the new code. Collectible AssemblyLoadContext — which would allow live reload — is .NET Core
    /// only, so this is a platform limit, not an oversight. It is documented in AUTHORING.md.
    /// </summary>
    internal static class AiconScriptCompiler
    {
        private static readonly CSharpParseOptions ParseOptions =
            new CSharpParseOptions(LanguageVersion.Latest);

        // Building this list means reading every loaded DLL's metadata — expensive, and it cannot
        // change within a Revit session, so it is built once.
        private static IReadOnlyList<MetadataReference> _references;

        internal static ScriptCompileResult CompileRoutine(Routine routine)
        {
            var result = new ScriptCompileResult();
            if (routine.Script == null || routine.Script.Files == null || routine.Script.Files.Count == 0)
            {
                result.Errors.Add(Simple("AICON001", "This routine has no script files."));
                return result;
            }

            var sources = new List<KeyValuePair<string, string>>();
            foreach (string name in routine.Script.Files)
            {
                string path = Path.Combine(routine.FolderPath ?? "", Path.GetFileName(name));
                if (!File.Exists(path))
                {
                    result.Errors.Add(Simple("AICON002", "Script file not found: " + name));
                    return result;
                }
                try { sources.Add(new KeyValuePair<string, string>(Path.GetFileName(path), File.ReadAllText(path))); }
                catch (Exception ex)
                {
                    result.Errors.Add(Simple("AICON003", "Cannot read " + name + ": " + ex.Message));
                    return result;
                }
            }

            return Compile(sources, "AiconRoutine_" + routine.Id.Replace('-', '_'));
        }

        /// <summary>Compiles one snippet — used to check an AI's code BEFORE promoting it to a routine.</summary>
        internal static ScriptCompileResult CompileSnippet(string source, string assemblyName)
        {
            return Compile(
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Routine.cs", source) },
                assemblyName);
        }

        private static ScriptCompileResult Compile(List<KeyValuePair<string, string>> sources, string assemblyName)
        {
            var result = new ScriptCompileResult();

            var trees = new List<SyntaxTree>();
            foreach (KeyValuePair<string, string> s in sources)
            {
                string text = IsFullRoutine(s.Value) ? s.Value : WrapBody(s.Value);
                // The encoding argument is REQUIRED: emitting a PDB for a tree with no encoding fails
                // with CS8055, which is a baffling error to hit for an unrelated reason.
                trees.Add(CSharpSyntaxTree.ParseText(text, ParseOptions, s.Key, Encoding.UTF8));
            }

            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                GetReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: false));      // deliberately not configurable

            using (var assemblyStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                EmitResult emit = compilation.Emit(assemblyStream, pdbStream);
                if (!emit.Success)
                {
                    foreach (Diagnostic d in emit.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Take(25))
                    {
                        // GetMappedLineSpan, NOT GetLineSpan: only the mapped one applies the
                        // "#line 1" directive emitted by WrapBody. With GetLineSpan the author is told
                        // "error on line 34" for a six-line routine, which is exactly the confusion the
                        // directive exists to prevent.
                        FileLinePositionSpan span = d.Location.GetMappedLineSpan();
                        result.Errors.Add(new ScriptDiagnostic
                        {
                            Id = d.Id,
                            Message = d.GetMessage(),
                            File = string.IsNullOrEmpty(span.Path) ? "script.cs" : span.Path,
                            Line = span.StartLinePosition.Line + 1,
                            Column = span.StartLinePosition.Character + 1
                        });
                    }
                    if (result.Errors.Count == 0)
                        result.Errors.Add(Simple("AICON004", "Compilation failed without a specific error."));
                    return result;
                }

                try
                {
                    result.Assembly = Assembly.Load(assemblyStream.ToArray(), pdbStream.ToArray());
                }
                catch (Exception ex)
                {
                    result.Errors.Add(Simple("AICON005", "Compiled, but the assembly could not be loaded: " + ex.Message));
                }
                return result;
            }
        }

        /// <summary>True when the author already wrote a class implementing IAiconRoutine, so it should
        /// be compiled as-is rather than wrapped.</summary>
        internal static bool IsFullRoutine(string source)
        {
            try
            {
                SyntaxNode root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();
                return root.DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .Any(t => t.BaseList != null &&
                              t.BaseList.Types.Any(bt => bt.Type.ToString().Contains("IAiconRoutine")));
            }
            catch
            {
                return false;   // unparseable → treat as a body and let the real compile report why
            }
        }

        /// <summary>
        /// Wraps a bare statement body into a full routine class.
        ///
        /// The `#line 1 "Routine.cs"` directive is the important part: without it Roslyn reports errors
        /// at their position in this generated wrapper, so a six-line routine gets told "error on line
        /// 34" — which sends an AI chasing a line that does not exist in what it wrote. With it, every
        /// diagnostic points at the author's own line numbers.
        /// </summary>
        internal static string WrapBody(string body)
        {
            const string header =
                "using System;\n" +
                "using System.Collections;\n" +
                "using System.Collections.Generic;\n" +
                "using System.Linq;\n" +
                "using Autodesk.Revit.DB;\n" +
                "using Autodesk.Revit.DB.Architecture;\n" +
                "using Autodesk.Revit.DB.Structure;\n" +
                "using Autodesk.Revit.UI;\n" +
                "using AICon.Routines;\n" +
                "\n" +
                "public sealed class GeneratedRoutine : IAiconRoutine\n" +
                "{\n" +
                "    public object Run(UIApplication uiapp, UIDocument uidoc, Document doc, RoutineInputs input)\n" +
                "    {\n";

            const string footer =
                "\n        return null;\n" +
                "    }\n" +
                "}\n";

            return header + "#line 1 \"Routine.cs\"\n" + body + "\n#line default\n" + footer;
        }

        /// <summary>
        /// References = every non-dynamic assembly already loaded in the Revit process. That means a
        /// routine compiles against the EXACT RevitAPI.dll this Revit is running — no reference
        /// assemblies to ship, no version drift between Revit 2023 and 2024, nothing to configure.
        /// </summary>
        private static IReadOnlyList<MetadataReference> GetReferences()
        {
            if (_references != null) return _references;

            var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                string location;
                try { location = assembly.Location; }
                catch { continue; }
                if (string.IsNullOrEmpty(location) || !File.Exists(location)) continue;

                string name = assembly.GetName().Name ?? location;
                if (refs.ContainsKey(name)) continue;
                try { refs[name] = MetadataReference.CreateFromFile(location); }
                catch { /* unreadable assembly — skip it rather than fail the whole compile */ }
            }

            TryAddByName(refs, "netstandard");
            TryAddByName(refs, "System.Runtime");

            _references = refs.Values.ToList();
            return _references;
        }

        private static void TryAddByName(Dictionary<string, MetadataReference> refs, string simpleName)
        {
            if (refs.ContainsKey(simpleName)) return;
            try
            {
                Assembly a = Assembly.Load(simpleName);
                if (!a.IsDynamic && File.Exists(a.Location))
                    refs[simpleName] = MetadataReference.CreateFromFile(a.Location);
            }
            catch { /* facade unavailable — most routines compile without it */ }
        }

        private static ScriptDiagnostic Simple(string id, string message) =>
            new ScriptDiagnostic { Id = id, Message = message, File = "routine.json", Line = 1, Column = 1 };
    }
}
