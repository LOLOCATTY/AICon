using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AICon
{
    // A small, light-themed modal dialog shown inside Revit the first time an agent that needs an API
    // key is selected (Gemini / DeepSeek / …). The user pastes the key; it is stored in aiconagent.json
    // so it is never requested again. Code-only WPF (no XAML), colours set explicitly so it stays
    // readable regardless of Revit's dark or light theme.
    internal sealed class ApiKeyPromptWindow : Window
    {
        private readonly TextBox _box;

        // The key the user entered, valid only when ShowDialog() returned true.
        public string ApiKey { get; private set; }

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Faint = Brush(0x9A, 0xA0, 0xA6);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);

        public ApiKeyPromptWindow(string agentLabel, string provider)
        {
            Title = agentLabel + " API key";
            Width = 470;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.White;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "Enter your " + agentLabel + " API key",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                Margin = new Thickness(0, 0, 0, 4)
            });
            root.Children.Add(new TextBlock
            {
                Text = HintFor(provider),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var inputBorder = new Border
            {
                BorderBrush = BorderClr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                Padding = new Thickness(8, 5, 8, 5)
            };
            _box = new TextBox
            {
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                Foreground = Ink,
                Background = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            _box.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { TrySave(); e.Handled = true; }
            };
            inputBorder.Child = _box;
            root.Children.Add(inputBorder);

            root.Children.Add(new TextBlock
            {
                Text = "Stored locally in aiconagent.json on this PC. You won't be asked again.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Faint,
                Margin = new Thickness(0, 8, 0, 16)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var save = new Button
            {
                Content = "Save",
                MinWidth = 88,
                Padding = new Thickness(10, 6, 10, 6),
                IsDefault = true,
                Foreground = Brushes.White,
                Background = Accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            save.Click += (s, e) => TrySave();
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (s, e) => { _box.Focus(); };
        }

        private void TrySave()
        {
            string k = (_box.Text ?? "").Trim();
            if (k.Length == 0) { _box.Focus(); return; }
            ApiKey = k;
            DialogResult = true;   // closes the dialog
        }

        private static string HintFor(string provider)
        {
            string p = (provider ?? "").ToLowerInvariant();
            if (p == "gemini" || p == "google")
                return "Get a free key from Google AI Studio (aistudio.google.com/apikey). " +
                       "Keys look like AIza… or AQ.…";
            return "Get a key from your provider's dashboard (for DeepSeek: platform.deepseek.com). " +
                   "Keys usually look like sk-…";
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
