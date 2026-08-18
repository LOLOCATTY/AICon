using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace AICon.Routines
{
    // Builds the "Routines" ribbon panel at startup.
    //
    // Revit only permits ribbon construction during OnStartup, so the number of per-routine buttons is
    // fixed for the session. That is handled honestly rather than hidden: a fixed set of slots is
    // created and bound to whatever routines exist at startup, and the always-current browser button
    // covers everything else (including routines saved while Revit is running).
    internal static class RoutineRibbonBuilder
    {
        internal const int MaxButtons = 8;

        // slot index -> routine id, decided at startup.
        private static readonly Dictionary<int, string> SlotBindings = new Dictionary<int, string>();

        internal static IReadOnlyCollection<string> ButtonRoutineIds => SlotBindings.Values.ToList();

        internal static string IdForSlot(int slot)
        {
            string id;
            return SlotBindings.TryGetValue(slot, out id) ? id : null;
        }

        internal static void Build(UIControlledApplication application, string assemblyPath, string iconDir)
        {
            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(App.ProductName))
                if (p.Name == "Routines") { panel = p; break; }
            if (panel == null) panel = application.CreateRibbonPanel(App.ProductName, "Routines");

            var browse = new PushButtonData("AICon_Routines", "Routines", assemblyPath,
                "AICon.Routines.RoutineBrowserCommand")
            {
                ToolTip = "Run a saved routine",
                LongDescription = "Lists every routine installed on this machine (yours and any team folders) and " +
                                  "runs the one you pick. Always current — a routine saved a moment ago appears here " +
                                  "immediately, without restarting Revit."
            };
            SetIcon(browse, iconDir, "routine");
            panel.AddItem(browse);

            var folder = new PushButtonData("AICon_RoutinesFolder", "Open\nFolder", assemblyPath,
                "AICon.Routines.OpenRoutinesFolderCommand")
            { ToolTip = "Open the routines folder (copy a folder here to share a routine)" };
            SetIcon(folder, iconDir, "log");

            var reload = new PushButtonData("AICon_RoutinesReload", "Reload", assemblyPath,
                "AICon.Routines.ReloadRoutinesCommand")
            { ToolTip = "Re-read the routines folder and report what is installed" };
            SetIcon(reload, iconDir, "about");

            panel.AddStackedItems(folder, reload);

            AddRoutineButtons(panel, assemblyPath, iconDir);
        }

        private static void AddRoutineButtons(RibbonPanel panel, string assemblyPath, string iconDir)
        {
            List<Routine> routines;
            List<string> problems;
            try { routines = RoutineStore.LoadAll(out problems); }
            catch (Exception ex) { App.Log("Routines: startup scan failed: " + ex.Message); return; }

            if (problems != null && problems.Count > 0)
                App.Log("Routines skipped at startup: " + string.Join(" | ", problems));

            // Only routines that asked for a button get one, oldest-configured first, up to the slot count.
            List<Routine> wantButtons = routines.Where(r => r.Ribbon != null).Take(MaxButtons).ToList();

            for (int slot = 0; slot < wantButtons.Count; slot++)
            {
                Routine r = wantButtons[slot];
                string commandClass = "AICon.Routines.RoutineSlot" + slot + "Command";
                string caption = !string.IsNullOrWhiteSpace(r.Ribbon.ButtonText)
                    ? r.Ribbon.ButtonText.Replace("\\n", "\n")
                    : r.Name;

                var data = new PushButtonData("AICon_Routine_" + slot, caption, assemblyPath, commandClass)
                {
                    ToolTip = !string.IsNullOrWhiteSpace(r.Ribbon.Tooltip) ? r.Ribbon.Tooltip : r.Description,
                    LongDescription = r.Description
                };

                // A routine may ship its own icon next to routine.json.
                bool custom = false;
                if (!string.IsNullOrWhiteSpace(r.Ribbon.Icon) && r.FolderPath != null)
                {
                    string iconPath = Path.Combine(r.FolderPath, r.Ribbon.Icon);
                    if (File.Exists(iconPath))
                    {
                        try { data.LargeImage = Load(iconPath); custom = true; } catch { }
                    }
                }
                if (!custom) SetIcon(data, iconDir, "routine");

                try
                {
                    panel.AddItem(data);
                    SlotBindings[slot] = r.Id;
                }
                catch (Exception ex) { App.Log("Routines: could not add button for " + r.Id + ": " + ex.Message); }
            }
        }

        private static void SetIcon(PushButtonData button, string iconDir, string baseName)
        {
            try
            {
                string large = Path.Combine(iconDir, baseName + "32.png");
                string small = Path.Combine(iconDir, baseName + "16.png");
                if (File.Exists(large)) button.LargeImage = Load(large);
                if (File.Exists(small)) button.Image = Load(small);
            }
            catch { /* a missing icon is not worth failing startup over */ }
        }

        // Loaded fully into memory so the PNG file is not left locked while Revit runs (same reason as App.LoadImage).
        private static BitmapImage Load(string path)
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
    }
}
