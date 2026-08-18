using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json.Nodes;
using AICon.Agent;
using AICon.Shared;
// 'Agent' is both a namespace (AICon.Agent) and the loop type (AICon.Agent.Agent); alias the type
// so it is unambiguous from inside namespace AICon.
using AgentCore = AICon.Agent.Agent;

namespace AICon
{
    // The in-Revit chat surface. A plain WPF UserControl (built in code, no XAML) that hosts the SAME
    // Agent loop the console uses, but wired to InProcessToolExecutor so tools run directly inside
    // Revit. The AI backend is whatever aiconagent.json selects (Gemini, a local Ollama model, …).
    // Styled as a light theme with explicit colours so it stays readable regardless of Revit's dark
    // or light UI theme (the pane would otherwise inherit dark-on-dark and be unreadable).
    internal sealed class ChatPanelControl : UserControl
    {
        private readonly StackPanel _messages;
        private readonly ScrollViewer _scroller;
        private readonly TextBox _input;
        private readonly Button _send;
        private readonly TextBlock _subtitle;

        private AgentCore _agent;           // created lazily on first send (so config errors show in-chat)
        private string _activeProfile;      // which ribbon agent is selected (null = file's default / flat)
        private bool _busy;
        private CancellationTokenSource _cts;   // cancels the request currently in flight (Stop / switch)
        private readonly System.Text.StringBuilder _transcript = new System.Text.StringBuilder();
        private InProcessToolExecutor _executor;   // current agent's executor (undo tracking, gates)
        private CheckBox _readOnlyBox;
        private volatile bool _readOnly;    // mirrored from the checkbox — read from worker threads
        private readonly List<string> _pendingFiles = new List<string>();   // attachments for the next message

        // Palette (light theme).
        private static readonly Color PageBg = Color.FromRgb(0xFB, 0xFB, 0xFD);
        private static readonly Color InkColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
        private static readonly Color MutedColor = Color.FromRgb(0x6B, 0x70, 0x76);

        private static readonly Brush PageBrush = Freeze(new SolidColorBrush(PageBg));
        private static readonly Brush Ink = Freeze(new SolidColorBrush(InkColor));
        private static readonly Brush Muted = Freeze(new SolidColorBrush(MutedColor));
        private static readonly Brush HeaderLine = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xE8)));
        private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0xD1)));
        private static readonly Brush InputBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD2)));
        private static readonly Brush StopBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)));
        private static readonly FontFamily Mono = new FontFamily("Consolas, Cascadia Mono, Courier New");

        public ChatPanelControl()
        {
            Background = PageBrush;

            var grid = new Grid { Margin = new Thickness(10, 8, 10, 10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // messages
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // input

            // --- header -------------------------------------------------------------------------
            var headerStack = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
            headerStack.Children.Add(new TextBlock
            {
                Text = "AICon Chat",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Foreground = Ink
            });
            _subtitle = new TextBlock
            {
                Text = "connected to the open Revit model",
                FontSize = 11,
                Foreground = Muted,
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            headerStack.Children.Add(_subtitle);

            // Safety controls: read-only toggle (blocks all model edits) + undo-last-creation button.
            var controlsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _readOnlyBox = new CheckBox
            {
                Content = "Read-only (no model changes)",
                FontSize = 11,
                Foreground = Muted,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _readOnlyBox.Checked += (s, e) => _readOnly = true;
            _readOnlyBox.Unchecked += (s, e) => _readOnly = false;
            var undoBtn = new Button
            {
                Content = "↩ Undo last",
                FontSize = 11,
                Margin = new Thickness(14, 0, 0, 0),
                Padding = new Thickness(8, 1, 8, 2),
                Background = Brushes.White,
                Foreground = Ink,
                BorderBrush = InputBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Deletes the elements the last AI turn CREATED. (Modifications and deletions: use Ctrl+Z in Revit.)"
            };
            undoBtn.Click += (s, e) => UndoLast();
            controlsRow.Children.Add(_readOnlyBox);
            controlsRow.Children.Add(undoBtn);
            headerStack.Children.Add(controlsRow);

            headerStack.Children.Add(new Border { Height = 1, Background = HeaderLine, Margin = new Thickness(0, 8, 0, 0) });
            Grid.SetRow(headerStack, 0);
            grid.Children.Add(headerStack);

            // --- messages -----------------------------------------------------------------------
            _messages = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            _scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _messages
            };
            Grid.SetRow(_scroller, 1);
            grid.Children.Add(_scroller);

            // --- input --------------------------------------------------------------------------
            var inputGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // attach
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // send

            var inputBorder = new Border
            {
                BorderBrush = InputBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                Padding = new Thickness(6, 2, 6, 2)
            };
            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinLines = 1,
                MaxLines = 5,
                FontSize = 13,
                Foreground = Ink,
                Background = Brushes.White,
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _input.KeyDown += OnInputKeyDown;
            inputBorder.Child = _input;
            Grid.SetColumn(inputBorder, 0);
            inputGrid.Children.Add(inputBorder);

            _send = new Button
            {
                Content = "Send",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 64,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = AccentBrush,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            // Attach files (images / PDF / Excel / CSV) to the next message.
            var attach = new Button
            {
                Content = "📎",   // 📎
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 13,
                Background = Brushes.White,
                Foreground = Ink,
                BorderBrush = InputBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Attach files: images, PDF (Gemini), Excel/CSV — the AI reads them with your next message"
            };
            attach.Click += (s, e) => PickAttachments();
            Grid.SetColumn(attach, 1);
            inputGrid.Children.Add(attach);

            // While a request is running the button is a Stop control (cancels the in-flight turn);
            // otherwise it sends. It is always enabled so the panel can never look "dead".
            _send.Click += (s, e) => { if (_busy) CancelActive(); else Submit(); };
            Grid.SetColumn(_send, 2);
            inputGrid.Children.Add(_send);

            Grid.SetRow(inputGrid, 2);
            grid.Children.Add(inputGrid);

            // Right-click anywhere in the message area to copy the whole conversation.
            var copyAll = new MenuItem { Header = "Copy all conversation" };
            copyAll.Click += (s, e) => CopyTranscript();
            _scroller.ContextMenu = new ContextMenu();
            _scroller.ContextMenu.Items.Add(copyAll);

            Content = grid;

            AddBubble(Kind.Info, "AICon",
                "Ask about the open Revit model in plain language — e.g. \"how many levels are in the project?\" " +
                "or \"color the walls by fire rating\". Press Enter to send, Shift+Enter for a new line.");
        }

        private enum Kind { User, Ai, Tool, Error, Info }

        // Enter sends; Shift+Enter inserts a newline.
        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                Submit();
            }
        }

        // Pick files to ride along with the next message.
        private void PickAttachments()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Attach to AICon chat",
                Filter = "Supported (images, PDF, Excel, CSV)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf;*.xlsx;*.xlsm;*.csv;*.txt;*.md;*.json|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            foreach (string f in dlg.FileNames) _pendingFiles.Add(f);
            AddBubble(Kind.Info, "attachments",
                "Will be sent with your next message:\n  " +
                string.Join("\n  ", _pendingFiles.Select(System.IO.Path.GetFileName)) +
                (_pendingFiles.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    ? "\nNote: PDFs can only be READ by the Gemini agent." : ""));
        }

        // Convert the pending files into what the providers understand: spreadsheets/text are inlined
        // into the message text; images and PDFs become binary attachments.
        private List<ChatAttachment> ConsumePendingFiles(ref string text)
        {
            if (_pendingFiles.Count == 0) return null;
            var atts = new List<ChatAttachment>();
            foreach (string f in _pendingFiles)
            {
                string name = System.IO.Path.GetFileName(f);
                try
                {
                    string inline = FileTexts.TryReadAsText(f);
                    if (inline != null)
                    {
                        text += "\n\n[ATTACHED FILE: " + name + "]\n" + inline + "\n[END OF FILE]";
                        continue;
                    }
                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    string mime =
                        ext == ".png" ? "image/png" :
                        ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" :
                        ext == ".webp" ? "image/webp" :
                        ext == ".gif" ? "image/gif" :
                        ext == ".pdf" ? "application/pdf" : null;
                    if (mime == null)
                    {
                        AddBubble(Kind.Error, "attachment", name + ": unsupported file type — skipped.");
                        continue;
                    }
                    if (new FileInfo(f).Length > 10_000_000)
                    {
                        AddBubble(Kind.Error, "attachment", name + " is larger than 10 MB — skipped.");
                        continue;
                    }
                    atts.Add(new ChatAttachment
                    {
                        FileName = name,
                        MimeType = mime,
                        Base64Data = Convert.ToBase64String(File.ReadAllBytes(f))
                    });
                }
                catch (Exception ex)
                {
                    AddBubble(Kind.Error, "attachment", name + ": " + ex.Message);
                }
            }
            _pendingFiles.Clear();
            return atts.Count > 0 ? atts : null;
        }

        private void Submit()
        {
            if (_busy) return;
            string text = (_input.Text ?? "").Trim();
            if (text.Length == 0 && _pendingFiles.Count == 0) return;
            if (text.Length == 0) text = "Read the attached file(s) and act on them.";

            if (!EnsureAgent()) return;   // shows its own error bubble on failure

            _input.Text = "";
            string shown = text;
            if (_pendingFiles.Count > 0)
                shown += "\n📎 " + string.Join(", ", _pendingFiles.Select(System.IO.Path.GetFileName));
            AddBubble(Kind.User, "You", shown);

            List<ChatAttachment> atts = ConsumePendingFiles(ref text);

            SetBusy(true);
            _executor?.BeginTurn();   // new turn — forget the previous turn's undo candidates

            // Capture the current agent + token so a mid-turn provider switch (from the ribbon) or a
            // Stop click doesn't disturb the request already in flight; the next message uses whatever
            // agent is current then.
            var cts = new CancellationTokenSource();
            _cts = cts;
            AgentCore agent = _agent;
            string finalText = text;
            Task.Run(() => agent.SendAsync(finalText, atts, cts.Token))
                .ContinueWith(t =>
                {
                    // Ignore a stale completion if a newer request (or a switch/stop) has superseded us.
                    if (!ReferenceEquals(_cts, cts)) { cts.Dispose(); return; }

                    if (t.IsCanceled)
                    {
                        AddBubble(Kind.Info, "AICon", "Stopped.");
                    }
                    else if (t.IsFaulted)
                    {
                        Exception ex = t.Exception != null && t.Exception.InnerException != null
                            ? t.Exception.InnerException : t.Exception;
                        if (ex is OperationCanceledException)
                            AddBubble(Kind.Info, "AICon", "Stopped.");
                        else
                            AddBubble(Kind.Error, "Error", ex != null ? ex.Message : "unknown error");
                    }
                    _cts = null;
                    cts.Dispose();
                    SetBusy(false);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Cancel the request currently in flight (Stop button, or when switching agents). Safe to call
        // when nothing is running.
        private void CancelActive()
        {
            try { _cts?.Cancel(); } catch { /* already disposed / completed — nothing to stop */ }
        }

        // Yes/No gate shown when the model asks to delete elements. Called from a worker thread.
        private bool ConfirmOnUi(string title, string message)
        {
            return Dispatcher.Invoke(() =>
                MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    == MessageBoxResult.Yes);
        }

        // Delete the elements the last AI turn created (the honest "undo": modifications and
        // deletions cannot be rolled back through the API — those are Ctrl+Z territory).
        private void UndoLast()
        {
            if (_busy) { AddBubble(Kind.Info, "AICon", "Wait for the current request to finish first."); return; }
            var exec = _executor;
            var ids = exec != null ? exec.CreatedIds : null;
            if (ids == null || ids.Count == 0)
            {
                AddBubble(Kind.Info, "AICon",
                    "The last AI turn created no elements — nothing to undo here. " +
                    "For modifications or deletions use Ctrl+Z in Revit itself.");
                return;
            }
            if (MessageBox.Show("Delete the " + ids.Count + " element(s) created by the last AI turn?",
                    "AICon — undo last", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string json = "{\"ids\":[" + string.Join(",", ids) + "]}";
            SetBusy(true);
            AddBubble(Kind.Tool, "undo", "delete_elements " + json);
            Task.Run(() => exec.ExecuteDirect("delete_elements", json))
                .ContinueWith(t =>
                {
                    AddBubble(t.IsFaulted ? Kind.Error : Kind.Tool, "result",
                        t.IsFaulted ? (t.Exception != null ? t.Exception.InnerException?.Message ?? "error" : "error")
                                    : t.Result);
                    exec.BeginTurn();   // don't offer the same undo twice
                    SetBusy(false);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Switch the active agent from the ribbon (Gemini / DeepSeek / Local). Rebuilds the backend for
        // the chosen profile; a request already running keeps its captured agent, new messages use this
        // one. Because each agent is a separate model, switching starts a fresh conversation.
        public void SwitchProvider(string profileKey)
        {
            OnUi(() =>
            {
                // Any request in flight belongs to the old agent — cancel it and unlock the input so the
                // panel can never get stuck showing a disabled box / spinning Send button.
                CancelActive();
                _cts = null;
                SetBusy(false);

                if (string.Equals(_activeProfile, profileKey, StringComparison.OrdinalIgnoreCase) && _agent != null)
                {
                    AddBubble(Kind.Info, "AICon", Config.Label(profileKey) + " is already active.");
                    return;
                }
                _activeProfile = profileKey;
                _agent = null;                 // force a rebuild for the newly selected backend
                if (BuildAgent())
                    AddBubble(Kind.Info, "AICon",
                        Config.Label(profileKey) + " is now active. New messages use it (fresh conversation).");
                // BuildAgent shows its own error bubble on failure (e.g. missing API key).
            });
        }

        // Build the Agent from aiconagent.json on first use. Returns false (and reports) if unconfigured.
        private bool EnsureAgent()
        {
            if (_agent != null) return true;
            return BuildAgent();
        }

        // (Re)build the agent for the currently selected profile. Reports config problems in-chat.
        private bool BuildAgent()
        {
            try
            {
                string usedPath;
                Config root = Config.Load(null, out usedPath);
                string profile = _activeProfile ?? root.ActiveProfile;   // may be null → flat/legacy
                Config cfg = root.ForProfile(profile);
                _activeProfile = profile;

                if (Config.RequiresApiKey(cfg) && string.IsNullOrWhiteSpace(cfg.ResolveApiKey()))
                {
                    // Ask for the key right inside Revit, then store it so it's never requested again.
                    string entered = PromptForApiKey(profile, cfg.Provider);
                    if (string.IsNullOrWhiteSpace(entered))
                    {
                        AddBubble(Kind.Info, "AICon",
                            Config.Label(profile) + " needs an API key to start. Click its button again when you have one.");
                        return false;
                    }
                    Config.SaveApiKey(profile, entered);
                    root = Config.Load(null, out usedPath);   // reload so the saved key takes effect
                    cfg = root.ForProfile(profile);
                    AddBubble(Kind.Info, "AICon", Config.Label(profile) + " API key saved.");
                }

                IProvider provider = ProviderFactory.Create(cfg);
                provider.Status = msg => OnUi(() => AddBubble(Kind.Info, "status", msg));

                // Small local models (Ollama on localhost) get a curated tool subset and a short
                // example-driven prompt — 7–8B models drown in the full 57-tool catalogue and reach
                // for run_code with broken C#. Cloud models keep the full set.
                bool isLocalModel = cfg.BaseUrl != null &&
                    (cfg.BaseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     cfg.BaseUrl.IndexOf("127.0.0.1", StringComparison.Ordinal) >= 0);
                JsonArray tools = isLocalModel ? Tools.BuildLocalToolList() : Tools.BuildToolList();
                string systemPrompt = isLocalModel ? AgentCore.LocalSystemPrompt() : AgentCore.DefaultSystemPrompt();

                // Executor with the panel's safety gates (read-only toggle, delete confirmation) and
                // created-id tracking for the undo button.
                var executor = new InProcessToolExecutor(() => _readOnly, ConfirmOnUi);
                _executor = executor;

                // Live project snapshot, injected into the system prompt at the first message so the
                // model knows the real level/view/template names. (A few-shot seeded exchange was
                // tried and REMOVED: small models leak it into replies and imitate its "extra work".)
                Func<Task<string>> snapshot = () => executor.ExecuteAsync("__snapshot", "{}", CancellationToken.None);
                _agent = new AgentCore(provider, tools, executor, systemPrompt, 25, null, snapshot);

                _agent.AssistantText += t => OnUi(() => AddBubble(Kind.Ai, "AI", t));
                _agent.ToolStarted += (name, argsPreview) => OnUi(() => AddBubble(Kind.Tool, "run " + name, argsPreview));
                _agent.ToolFinished += preview => OnUi(() => AddBubble(Kind.Tool, "result", preview));
                _agent.Notice += note => OnUi(() => AddBubble(Kind.Tool, "note", note));

                _subtitle.Text = provider.Name + "  •  connected to the open Revit model";

                // Remember the selection (and seed discoverable per-agent placeholders) so the choice
                // survives a Revit restart. Best-effort — never block chatting on a failed write.
                try { Config.PersistActiveProfile(profile); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                AddBubble(Kind.Error, "Could not start AI backend",
                    ex.Message + "\nCheck aiconagent.json (provider / model / apiKey).");
                return false;
            }
        }

        // Show the in-Revit key dialog for the selected agent. Returns the entered key, or null if the
        // user cancelled. Runs on the UI thread (BuildAgent is always invoked there).
        private string PromptForApiKey(string profile, string provider)
        {
            try
            {
                var dialog = new ApiKeyPromptWindow(Config.Label(profile), provider);
                bool? ok = dialog.ShowDialog();
                return ok == true ? dialog.ApiKey : null;
            }
            catch (Exception ex)
            {
                AddBubble(Kind.Error, "Could not open the key dialog", ex.Message);
                return null;
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            // The input box is NEVER disabled — the user can always type, select and copy, even while a
            // reply is being generated. The button stays enabled too, switching to a red "Stop" while busy.
            _input.IsEnabled = true;
            _send.IsEnabled = true;
            _send.Content = busy ? "Stop" : "Send";
            _send.Background = busy ? StopBrush : AccentBrush;
            if (!busy) _input.Focus();
        }

        private void AddBubble(Kind kind, string who, string text)
        {
            Color accent, bg;
            Brush textBrush = Ink;
            bool mono = false;

            switch (kind)
            {
                case Kind.User:
                    accent = Color.FromRgb(0x2E, 0x7D, 0xD1); bg = Color.FromRgb(0xEA, 0xF3, 0xFC);
                    break;
                case Kind.Ai:
                    accent = Color.FromRgb(0x1E, 0x8E, 0x5A); bg = Color.FromRgb(0xEA, 0xF7, 0xEF);
                    break;
                case Kind.Tool:
                    accent = Color.FromRgb(0x9A, 0xA0, 0xA6); bg = Color.FromRgb(0xF2, 0xF3, 0xF5);
                    textBrush = Muted; mono = true;
                    break;
                case Kind.Error:
                    accent = Color.FromRgb(0xC0, 0x39, 0x2B); bg = Color.FromRgb(0xFD, 0xEC, 0xEA);
                    textBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26)));
                    break;
                default: // Info
                    accent = Color.FromRgb(0x9A, 0xA0, 0xA6); bg = Color.FromRgb(0xF6, 0xF7, 0xF9);
                    textBrush = Muted;
                    break;
            }

            var accentBrush = Freeze(new SolidColorBrush(accent));
            var border = new Border
            {
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(10, 7, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = Freeze(new SolidColorBrush(bg)),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0)   // left accent bar
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = who,
                FontWeight = FontWeights.SemiBold,
                FontSize = 10.5,
                Foreground = accentBrush,
                Margin = new Thickness(0, 0, 0, 3)
            });
            // A read-only, borderless, transparent TextBox rather than a TextBlock so every message is
            // selectable and copyable (Ctrl+C), while still looking like plain text.
            var body = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                IsTabStop = false,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = textBrush,
                Padding = new Thickness(0),
                FontSize = mono ? 11.5 : 13,
                Cursor = Cursors.IBeam,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            if (mono) body.FontFamily = Mono;
            stack.Children.Add(body);

            border.Child = stack;
            _messages.Children.Add(border);
            _scroller.ScrollToEnd();

            _transcript.Append(who).Append(": ").Append(text).Append("\r\n\r\n");
        }

        // Copy the entire conversation to the clipboard (right-click menu on the message area).
        private void CopyTranscript()
        {
            try
            {
                string all = _transcript.ToString().TrimEnd();
                if (all.Length > 0) Clipboard.SetText(all);
            }
            catch (Exception ex)
            {
                AddBubble(Kind.Error, "Could not copy", ex.Message);
            }
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
