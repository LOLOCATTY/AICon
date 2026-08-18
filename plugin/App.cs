using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace AICon
{
    public class App : IExternalApplication
    {
        public const string ProductName = "AICon";
        // Reads the version straight from the assembly (which MSBuild stamps from
        // AICon.csproj's <Version>) instead of a second hand-typed literal that has to be kept in
        // sync by hand. AICon.csproj has <GenerateAssemblyInfo>true</GenerateAssemblyInfo>, so this
        // attribute always exists.
        public static readonly string Version =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
        public const int Port = 55234;

        internal static BridgeServer Server;
        // The single in-Revit chat panel instance, so ribbon "agent" buttons can switch its backend live.
        internal static ChatPanelControl ChatPanel;
        // Shared UI-thread dispatch mechanism — used by BOTH the localhost bridge (Claude) and the
        // in-Revit chat panel (Gemini / local models) so tool code always runs the same way.
        internal static RevitEventHandler Handler;
        internal static ExternalEvent BridgeEvent;
        // Identity of the dockable chat pane (stable GUID so Revit remembers its docked position).
        internal static readonly DockablePaneId ChatPaneId =
            new DockablePaneId(new Guid("B4B6E1F2-6C1A-4E5D-9A21-2A7C4F8E3D10"));

        internal static string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AICon", "bridge.log");

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));

                // Cloud providers (Gemini/OpenAI) need TLS 1.2 from within Revit's .NET 4.8 host.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                Handler = new RevitEventHandler();
                BridgeEvent = ExternalEvent.Create(Handler);
                Server = new BridgeServer(Port, Handler, BridgeEvent);
                Server.Start();

                // Register the chat pane during startup (Revit requires this in OnStartup).
                try
                {
                    ChatPanel = new ChatPanelControl();
                    application.RegisterDockablePane(ChatPaneId, "AICon Chat", new ChatPaneProvider(ChatPanel));
                }
                catch (Exception ex) { Log("Chat pane registration failed: " + ex.Message); }

                BuildRibbon(application);
                Log(ProductName + " " + Version + " started, listening on port " + Port);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log("Startup FAILED: " + ex);
                TaskDialog.Show(ProductName, "Failed to start: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { Server?.Stop(); } catch { }
            return Result.Succeeded;
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(ProductName); }
            catch { /* tab already exists */ }

            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(ProductName))
                if (p.Name == "AI Connection") { panel = p; break; }
            if (panel == null)
                panel = application.CreateRibbonPanel(ProductName, "AI Connection");

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string iconDir = Path.Combine(Path.GetDirectoryName(assemblyPath), "icons");

            var chatButton = new PushButtonData("AICon_Chat", "AI\nChat", assemblyPath, "AICon.ShowChatCommand")
            {
                ToolTip = "Open the AICon chat panel",
                LongDescription = "Chat with an AI (Gemini, or a local model) that reads and edits this Revit model " +
                                  "right here inside Revit. The backend is chosen in aiconagent.json."
            };
            SetImages(chatButton, iconDir, "aicon");

            var statusButton = new PushButtonData("AICon_Status", "Status", assemblyPath, "AICon.StatusCommand")
            {
                ToolTip = "AICon — AI connection for Revit",
                LongDescription = "Shows the status of the AICon bridge that lets Claude read and edit this model. " +
                                  "Chat with Claude Desktop while Revit is open; every edit is a normal, undoable transaction."
            };
            SetImages(statusButton, iconDir, "aicon");

            var logButton = new PushButtonData("AICon_Log", "Open\nLog", assemblyPath, "AICon.OpenLogCommand")
            {
                ToolTip = "Open the AICon activity log."
            };
            SetImages(logButton, iconDir, "log");

            var aboutButton = new PushButtonData("AICon_About", "About", assemblyPath, "AICon.AboutCommand")
            {
                ToolTip = "About AICon."
            };
            SetImages(aboutButton, iconDir, "about");

            panel.AddItem(chatButton);
            panel.AddItem(statusButton);
            panel.AddStackedItems(logButton, aboutButton);

            BuildAgentPanel(application, assemblyPath, iconDir);
            BuildShopDrawingsPanel(application, assemblyPath, iconDir);

            // Routines: saved, reusable capabilities that show up as their own buttons.
            // Never let a bad routine folder stop the rest of the ribbon from being built.
            try { Routines.RoutineRibbonBuilder.Build(application, assemblyPath, iconDir); }
            catch (Exception ex) { Log("Routines panel failed: " + ex); }
        }

        // A third ribbon panel: shop-drawing starters. AR400 walks the user through picking
        // package(s) (Blockwork/Flooring/General Arrangement/Ceiling/…) then level(s), and
        // creates one plan view per package per level in a single undoable transaction.
        private static void BuildShopDrawingsPanel(UIControlledApplication application, string assemblyPath, string iconDir)
        {
            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(ProductName))
                if (p.Name == "Shop Drawings") { panel = p; break; }
            if (panel == null)
                panel = application.CreateRibbonPanel(ProductName, "Shop Drawings");

            var ar400 = new PushButtonData("AICon_AR400", "AR400", assemblyPath, "AICon.Ar400Command")
            {
                ToolTip = "Create architectural shop drawing views + sheets",
                LongDescription = "Pick one or more shop drawing packages (Blockwork, Flooring, General " +
                                  "Arrangement, Ceiling, …), level(s), an optional scope box, and a title " +
                                  "block — AR400 creates a view + sheet for each combination (numbers/names " +
                                  "can come from an Excel register), places the view on it, and runs that " +
                                  "package's saved automation (AR400 Settings): tags and a sheet schedule. " +
                                  "Everything happens in a single undoable transaction."
            };
            SetImages(ar400, iconDir, "ar400");
            panel.AddItem(ar400);

            var ar400Settings = new PushButtonData("AICon_AR400Settings", "AR400\nSettings", assemblyPath, "AICon.Ar400SettingsCommand")
            {
                ToolTip = "Configure AR400's per-package automation",
                LongDescription = "Choose, per shop drawing package, which view template to apply and " +
                                  "whether AR400 should auto-add room tags, wall tags, and a sheet schedule. " +
                                  "Saved to ar400profiles.json and reused by every future AR400 run."
            };
            SetImages(ar400Settings, iconDir, "ar400");
            panel.AddItem(ar400Settings);
        }

        // A second ribbon panel of quick-switch buttons: one per agent (Gemini / DeepSeek / Local).
        // Clicking one opens the chat pane and points it at that backend — no config editing needed.
        private static void BuildAgentPanel(UIControlledApplication application, string assemblyPath, string iconDir)
        {
            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(ProductName))
                if (p.Name == "AI Agent") { panel = p; break; }
            if (panel == null)
                panel = application.CreateRibbonPanel(ProductName, "AI Agent");

            var gemini = new PushButtonData("AICon_UseGemini", "Gemini", assemblyPath, "AICon.UseGeminiCommand")
            {
                ToolTip = "Use Google Gemini for the chat",
                LongDescription = "Switches the AICon chat to Google Gemini (cloud). Needs a Gemini API key in " +
                                  "the \"gemini\" profile of aiconagent.json."
            };
            SetImages(gemini, iconDir, "gemini");

            var deepseek = new PushButtonData("AICon_UseDeepSeek", "DeepSeek", assemblyPath, "AICon.UseDeepSeekCommand")
            {
                ToolTip = "Use DeepSeek for the chat",
                LongDescription = "Switches the AICon chat to DeepSeek (cloud). Needs a DeepSeek API key in " +
                                  "the \"deepseek\" profile of aiconagent.json."
            };
            SetImages(deepseek, iconDir, "deepseek");

            var local = new PushButtonData("AICon_UseLocal", "Local", assemblyPath, "AICon.UseLocalCommand")
            {
                ToolTip = "Use a local model (Ollama / LM Studio)",
                LongDescription = "Switches the AICon chat to a local model served on this machine " +
                                  "(default Ollama at http://localhost:11434). No API key, nothing leaves the PC."
            };
            SetImages(local, iconDir, "local");

            panel.AddItem(gemini);
            panel.AddItem(deepseek);
            panel.AddItem(local);
        }

        private static void SetImages(PushButtonData button, string iconDir, string baseName)
        {
            try
            {
                string large = Path.Combine(iconDir, baseName + "32.png");
                string small = Path.Combine(iconDir, baseName + "16.png");
                if (File.Exists(large)) button.LargeImage = LoadImage(large);
                if (File.Exists(small)) button.Image = LoadImage(small);
            }
            catch (Exception ex) { Log("Icon load failed for " + baseName + ": " + ex.Message); }
        }

        /// <summary>Load fully into memory so the PNG file is not kept locked while Revit runs.</summary>
        private static BitmapImage LoadImage(string path)
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

        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
















