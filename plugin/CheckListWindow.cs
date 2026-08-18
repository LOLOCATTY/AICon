using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AICon
{
    // Reusable "pick one or more from a list" modal — code-only WPF, light theme (same palette as
    // ApiKeyPromptWindow) so it stays readable regardless of Revit's dark/light UI theme.
    // Used by AR400 for its two-step package-then-level picker, but generic enough for any checklist.
    internal sealed class CheckListWindow : Window
    {
        private readonly List<CheckBox> _boxes = new List<CheckBox>();
        private bool _confirmed;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush RowAlt = Brush(0xF4, 0xF6, 0xF8);

        private CheckListWindow(string title, string prompt, IEnumerable<string> items, string confirmLabel,
            IntPtr ownerHandle, IEnumerable<string> preChecked)
        {
            Title = title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            var header = new TextBlock
            {
                Text = prompt,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var selectAll = LinkButton("Select all");
            var selectNone = LinkButton("Select none");
            selectAll.Click += (s, e) => { foreach (var b in _boxes) b.IsChecked = true; };
            selectNone.Click += (s, e) => { foreach (var b in _boxes) b.IsChecked = false; };
            links.Children.Add(selectAll);
            links.Children.Add(new TextBlock { Text = "   ·   ", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            links.Children.Add(selectNone);
            DockPanel.SetDock(links, Dock.Top);
            root.Children.Add(links);

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var confirm = new Button
            {
                Content = confirmLabel,
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6),
                IsDefault = true,
                Foreground = Brushes.White,
                Background = Accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            confirm.Click += (s, e) => { _confirmed = true; DialogResult = true; };
            buttonBar.Children.Add(cancel);
            buttonBar.Children.Add(confirm);
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var listBorder = new Border
            {
                BorderBrush = BorderClr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };
            var list = new StackPanel();
            var preCheckedSet = preChecked != null ? new HashSet<string>(preChecked) : null;
            int i = 0;
            foreach (string item in items)
            {
                var box = new CheckBox
                {
                    Content = item,
                    FontSize = 13,
                    Foreground = Ink,
                    Padding = new Thickness(6, 0, 0, 0),
                    Margin = new Thickness(10, 7, 10, 7),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = preCheckedSet != null && preCheckedSet.Contains(item)
                };
                box.MouseDoubleClick += (s, e) => { _confirmed = true; DialogResult = true; };
                var row = new Border { Background = (i % 2 == 1) ? RowAlt : Brushes.Transparent, Child = box };
                list.Children.Add(row);
                _boxes.Add(box);
                i++;
            }
            scroll.Content = list;
            listBorder.Child = scroll;
            root.Children.Add(listBorder);

            Content = root;
        }

        private static Button LinkButton(string text)
        {
            return new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Accent,
                Padding = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // Parents the window to Revit's main window instead of using Topmost=true. Topmost + no Owner
        // causes a real bug: Revit's window and the dialog fight over OS z-order/foreground status,
        // which can freeze mouse/keyboard routing to everything in the dialog except whichever control
        // currently has focus (most visible when clicking into a TextBox). Setting Owner makes the
        // dialog properly modal over Revit — no fight, no freeze. Falls back to Topmost only if no
        // handle was supplied (defensive — should not normally happen).
        internal static void SetOwnerOrTopmost(Window window, IntPtr ownerHandle)
        {
            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = ownerHandle;
            else
                window.Topmost = true;
        }

        /// <summary>
        /// Shows the picker modally. 'cancelled' is true only when the user clicked Cancel/closed the
        /// window — treat that as "stop, don't change anything". Returns the checked item texts in
        /// their original order when confirmed (an EMPTY list is a valid, deliberate "none of these"
        /// answer, distinct from cancelling — callers that require at least one item should check
        /// both cancelled and Count == 0).
        /// </summary>
        public static List<string> Pick(string title, string prompt, IEnumerable<string> items, string confirmLabel,
            out bool cancelled, IntPtr ownerHandle = default, IEnumerable<string> preChecked = null)
        {
            var win = new CheckListWindow(title, prompt, items, confirmLabel, ownerHandle, preChecked);
            bool? result = win.ShowDialog();
            cancelled = result != true || !win._confirmed;
            return cancelled ? null : win._boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        }

        /// <summary>
        /// Shows a single-choice (radio button) picker. 'noneLabel' is always offered first.
        /// 'cancelled' is true only when the user clicked Cancel/closed the window — the caller should
        /// treat that as "stop the whole operation". A confirmed pick of noneLabel returns cancelled=false
        /// and a null id (a valid, deliberate "no selection, but keep going" choice).
        /// </summary>
        public static string PickOne(string title, string prompt, IEnumerable<(string id, string label)> items,
            string noneLabel, string confirmLabel, out bool cancelled, IntPtr ownerHandle = default)
        {
            var all = new List<(string id, string label)> { (null, noneLabel) };
            all.AddRange(items);
            var win = new SingleChoiceWindow(title, prompt, all, confirmLabel, ownerHandle);
            bool? result = win.ShowDialog();
            cancelled = result != true || !win.Confirmed;
            return cancelled ? null : win.ChosenId;
        }
    }

    // Radio-button single-choice modal, sharing CheckListWindow's palette/chrome. Kept as a separate
    // class (rather than overloading CheckListWindow's checkbox rows) since WPF RadioButtons need a
    // shared GroupName and a distinct selection model from the multi-check list.
    internal sealed class SingleChoiceWindow : Window
    {
        private readonly List<RadioButton> _radios = new List<RadioButton>();
        private readonly List<string> _ids = new List<string>();
        internal bool Confirmed;
        internal string ChosenId;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush RowAlt = Brush(0xF4, 0xF6, 0xF8);

        public SingleChoiceWindow(string title, string prompt, List<(string id, string label)> items, string confirmLabel, IntPtr ownerHandle)
        {
            Title = title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            CheckListWindow.SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            var header = new TextBlock
            {
                Text = prompt,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var confirm = new Button
            {
                Content = confirmLabel,
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6),
                IsDefault = true,
                Foreground = Brushes.White,
                Background = Accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            confirm.Click += (s, e) => { Confirmed = true; DialogResult = true; };
            buttonBar.Children.Add(cancel);
            buttonBar.Children.Add(confirm);
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var listBorder = new Border
            {
                BorderBrush = BorderClr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };
            var list = new StackPanel();
            string groupName = "sc_" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < items.Count; i++)
            {
                var radio = new RadioButton
                {
                    Content = items[i].label,
                    GroupName = groupName,
                    FontSize = 13,
                    Foreground = Ink,
                    Padding = new Thickness(6, 0, 0, 0),
                    Margin = new Thickness(10, 7, 10, 7),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = i == 0
                };
                radio.MouseDoubleClick += (s, e) => { Confirmed = true; DialogResult = true; };
                var row = new Border { Background = (i % 2 == 1) ? RowAlt : Brushes.Transparent, Child = radio };
                list.Children.Add(row);
                _radios.Add(radio);
                _ids.Add(items[i].id);
            }
            scroll.Content = list;
            listBorder.Child = scroll;
            root.Children.Add(listBorder);

            Content = root;
            Closed += (s, e) =>
            {
                if (!Confirmed) return;
                int idx = _radios.FindIndex(r => r.IsChecked == true);
                ChosenId = idx >= 0 ? _ids[idx] : null;
            };
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
