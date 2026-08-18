using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AICon
{
    // Wraps the WPF chat control as a Revit dockable pane (docks to the right by default).
    internal sealed class ChatPaneProvider : IDockablePaneProvider
    {
        private readonly ChatPanelControl _control;
        public ChatPaneProvider(ChatPanelControl control) { _control = control; }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _control;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }

    // Ribbon button handler: reveal the AICon chat pane.
    [Transaction(TransactionMode.Manual)]
    public class ShowChatCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(App.ChatPaneId);
                pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Could not open AICon chat: " + ex.Message;
                return Result.Failed;
            }
        }
    }

    // Shared behaviour for the ribbon's per-agent buttons: open the chat pane and point it at the
    // agent this button represents. Each concrete command just names its profile key.
    public abstract class SwitchAgentCommandBase : IExternalCommand
    {
        protected abstract string ProfileKey { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(App.ChatPaneId);
                pane.Show();
                if (App.ChatPanel != null) App.ChatPanel.SwitchProvider(ProfileKey);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Could not switch AICon agent: " + ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class UseGeminiCommand : SwitchAgentCommandBase
    {
        protected override string ProfileKey => Agent.Config.ProfileGemini;
    }

    [Transaction(TransactionMode.Manual)]
    public class UseDeepSeekCommand : SwitchAgentCommandBase
    {
        protected override string ProfileKey => Agent.Config.ProfileDeepSeek;
    }

    [Transaction(TransactionMode.Manual)]
    public class UseLocalCommand : SwitchAgentCommandBase
    {
        protected override string ProfileKey => Agent.Config.ProfileLocal;
    }
}
