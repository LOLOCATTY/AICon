using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AICon
{
    [Transaction(TransactionMode.Manual)]
    public class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool running = App.Server != null && App.Server.IsRunning;
            var dialog = new TaskDialog(App.ProductName)
            {
                MainInstruction = running ? "AICon bridge is running" : "AICon bridge is NOT running",
                MainContent = running
                    ? "Claude Desktop is connected to this Revit session on port " + App.Port + ".\n\n" +
                      "Open Claude Desktop and just chat — for example:\n" +
                      "• \"What's in my Revit model?\"\n" +
                      "• \"Create a 6 m wall on Level 1\"\n" +
                      "• \"Show me a screenshot of the active view\"\n\n" +
                      "Requests handled this session: " + App.Server.RequestCount
                    : "Restart Revit to reload AICon, or check the log for errors.",
                FooterText = "Log: " + App.LogPath
            };
            dialog.Show();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class OpenLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!File.Exists(App.LogPath))
                File.WriteAllText(App.LogPath, "");
            Process.Start(new ProcessStartInfo(App.LogPath) { UseShellExecute = true });
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            new TaskDialog(App.ProductName)
            {
                MainInstruction = App.ProductName + " " + App.Version,
                MainContent = "AI connection for Autodesk Revit.\n\n" +
                              "AICon links your Revit session to Claude Desktop through the Model Context Protocol, " +
                              "so you can read, analyze, and edit the open model by chatting in plain language.\n\n" +
                              "Every change Claude makes is a normal Revit transaction — one Ctrl+Z undoes it.",
                FooterText = "© 2026 Hossam Yousef"
            }.Show();
            return Result.Succeeded;
        }
    }
}
