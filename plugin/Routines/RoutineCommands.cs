using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AICon.Routines
{
    // Ribbon entry points for routines.
    //
    // A note on why there is a BROWSER rather than only per-routine buttons: Revit only lets an add-in
    // create ribbon panels and buttons during OnStartup. So a routine saved while Revit is running
    // cannot get its own button until the next restart. The browser sidesteps that completely — it
    // re-reads the routines folder every time it opens, so a routine the AI saved thirty seconds ago
    // is runnable immediately. Per-routine buttons are the convenience layer on top, built at startup
    // for whatever exists then.
    [Transaction(TransactionMode.Manual)]
    public class RoutineBrowserCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                IntPtr owner = commandData.Application.MainWindowHandle;
                List<string> problems;
                List<Routine> routines = RoutineStore.LoadAll(out problems);

                if (routines.Count == 0)
                {
                    var empty = new TaskDialog("AICon Routines")
                    {
                        MainInstruction = "No routines yet",
                        MainContent = "A routine is a saved capability that becomes a button.\n\n" +
                                      "Ask the AICon chat to build something, then tell it to save the routine — " +
                                      "or drop a routine folder into:\n" + RoutineStore.UserRoot,
                        FooterText = problems.Count > 0 ? problems.Count + " folder(s) were skipped — see the log." : null
                    };
                    empty.Show();
                    if (problems.Count > 0) App.Log("Routines skipped: " + string.Join(" | ", problems));
                    return Result.Succeeded;
                }

                var options = routines.Select(r => (r.Id, RoutineLabel(r)));
                bool cancelled;
                string chosenId = CheckListWindow.PickOne(
                    "AICon Routines", "Which routine do you want to run?",
                    options, "(none)", "Next", out cancelled, owner);
                if (cancelled || chosenId == null) return Result.Cancelled;

                Routine routine = routines.First(r => r.Id == chosenId);
                return RunRoutineInteractive(commandData.Application, routine, owner, ref message);
            }
            catch (Exception ex)
            {
                App.Log("Routine browser FAILED: " + ex);
                ShowFailure("AICon Routines", ex);
                return Result.Failed;
            }
        }

        private static string RoutineLabel(Routine r)
        {
            string tag = r.IsScript ? "  [script]" : "";
            if (r.Destructive) tag += "  [destructive]";
            else if (r.ReadOnly) tag += "  [read-only]";
            return r.Name + tag;
        }

        /// <summary>Prompt for inputs (if any) and run. Shared by the browser and per-routine buttons.</summary>
        internal static Result RunRoutineInteractive(UIApplication app, Routine routine, IntPtr owner, ref string message)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                TaskDialog.Show("AICon Routines", "Open a project first.");
                return Result.Cancelled;
            }

            Dictionary<string, object> inputs = RoutineInputWindow.Prompt(routine, uidoc, owner);
            if (inputs == null) return Result.Cancelled;   // user cancelled the form

            RoutineRunResult result = routine.IsScript
                ? RoutineScriptHost.Run(app, routine, inputs)
                : RoutineExecutor.Run(app, routine, inputs);

            if (!result.Success)
            {
                new TaskDialog("AICon Routines")
                {
                    MainInstruction = routine.Name + " did not finish",
                    MainContent = result.Error,
                    FooterText = "Nothing was left half-done."
                }.Show();
                return Result.Failed;
            }

            new TaskDialog("AICon Routines")
            {
                MainInstruction = routine.Name + " finished",
                MainContent = result.StepsRun + " step(s) ran.",
                FooterText = "One Ctrl+Z undoes the whole routine."
            }.Show();
            return Result.Succeeded;
        }

        internal static void ShowFailure(string title, Exception ex)
        {
            new TaskDialog(title)
            {
                MainInstruction = "Something went wrong",
                MainContent = ex.Message,
                FooterText = "Full details: " + App.LogPath
            }.Show();
        }
    }

    // Opens the routines folder in Explorer — the fastest way to share one with the team (copy the
    // folder) or to see what is installed.
    [Transaction(TransactionMode.Manual)]
    public class OpenRoutinesFolderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Directory.CreateDirectory(RoutineStore.UserRoot);
                Process.Start("explorer.exe", "\"" + RoutineStore.UserRoot + "\"");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                RoutineBrowserCommand.ShowFailure("AICon Routines", ex);
                return Result.Failed;
            }
        }
    }

    // Re-reads the routines folder and reports what is there now. Buttons created at startup can't be
    // added to at runtime (Revit limitation), so this refreshes what the BROWSER will show and tells
    // the user plainly which new routines need a restart to get their own button.
    [Transaction(TransactionMode.Manual)]
    public class ReloadRoutinesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                List<string> problems;
                List<Routine> routines = RoutineStore.LoadAll(out problems);
                int withButtons = RoutineRibbonBuilder.ButtonRoutineIds.Count;
                List<string> needRestart = routines
                    .Where(r => r.Ribbon != null && !RoutineRibbonBuilder.ButtonRoutineIds.Contains(r.Id))
                    .Select(r => r.Name).ToList();

                var body = new StringBuilder();
                body.AppendLine(routines.Count + " routine(s) found — all of them are runnable now from the Routines button.");
                if (withButtons > 0) body.AppendLine(withButtons + " have their own ribbon button.");
                if (needRestart.Count > 0)
                {
                    body.AppendLine();
                    body.AppendLine("These will get their own button after Revit restarts (Revit only allows " +
                                    "ribbon buttons to be created at startup):");
                    foreach (string n in needRestart.Take(10)) body.AppendLine("  •  " + n);
                }
                if (problems.Count > 0)
                {
                    body.AppendLine();
                    body.AppendLine("Skipped:");
                    foreach (string p in problems.Take(8)) body.AppendLine("  •  " + p);
                }

                new TaskDialog("AICon Routines") { MainInstruction = "Routines reloaded", MainContent = body.ToString().TrimEnd() }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Reload routines FAILED: " + ex);
                RoutineBrowserCommand.ShowFailure("AICon Routines", ex);
                return Result.Failed;
            }
        }
    }

    // One concrete command class per ribbon slot. Revit needs a distinct type name per button, and the
    // slot index is baked into the class name; at startup each slot is bound to a routine id.
    public abstract class RoutineSlotCommandBase : IExternalCommand
    {
        protected abstract int Slot { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string id = RoutineRibbonBuilder.IdForSlot(Slot);
                if (id == null)
                {
                    TaskDialog.Show("AICon Routines", "This button is not bound to a routine any more. Use the Routines button.");
                    return Result.Cancelled;
                }
                Routine routine = RoutineStore.FindById(id);
                if (routine == null)
                {
                    TaskDialog.Show("AICon Routines",
                        "The routine '" + id + "' is no longer in the routines folder — it may have been deleted or renamed.");
                    return Result.Cancelled;
                }
                return RoutineBrowserCommand.RunRoutineInteractive(
                    commandData.Application, routine, commandData.Application.MainWindowHandle, ref message);
            }
            catch (Exception ex)
            {
                App.Log("Routine slot " + Slot + " FAILED: " + ex);
                RoutineBrowserCommand.ShowFailure("AICon Routines", ex);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class RoutineSlot0Command : RoutineSlotCommandBase { protected override int Slot => 0; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot1Command : RoutineSlotCommandBase { protected override int Slot => 1; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot2Command : RoutineSlotCommandBase { protected override int Slot => 2; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot3Command : RoutineSlotCommandBase { protected override int Slot => 3; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot4Command : RoutineSlotCommandBase { protected override int Slot => 4; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot5Command : RoutineSlotCommandBase { protected override int Slot => 5; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot6Command : RoutineSlotCommandBase { protected override int Slot => 6; }
    [Transaction(TransactionMode.Manual)] public class RoutineSlot7Command : RoutineSlotCommandBase { protected override int Slot => 7; }
}
