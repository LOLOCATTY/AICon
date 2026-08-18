using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace AICon
{
    // Maps each Revit level to the way that level is written in the drawing register, for projects
    // where the two use different naming — e.g. the model says "-01-BF04-PVL" but the shop drawing
    // list says "BS04", or "00-GROUND" vs "GFL". Without this, AR400 cannot tell which register line
    // belongs to which sheet and falls back to Revit's own numbering.
    //
    // Filled in once and remembered in ar400profiles.json. Left blank, a level just matches on its
    // own name (which is correct whenever both sides already agree).
    internal sealed class LevelAliasWindow : Window
    {
        private sealed class AliasRow
        {
            public string LevelName;
            public TextBox Box;
        }

        private readonly List<AliasRow> _rows = new List<AliasRow>();
        private bool _confirmed;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush RowAlt = Brush(0xF4, 0xF6, 0xF8);
        private static readonly Brush HeaderBg = Brush(0xEE, 0xF1, 0xF5);

        private LevelAliasWindow(Document doc, Ar400Settings settings, IntPtr ownerHandle,
            Dictionary<string, string> suggestions, string introOverride)
        {
            Title = "AR400 — how levels are named in the drawing register";
            Width = 620;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            CheckListWindow.SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            root.Children.Add(DockedTo(new TextBlock
            {
                Text = introOverride ??
                       "If your shop drawing list writes levels differently from the model (e.g. the register " +
                       "says \"BS04\" or \"GFL\" where Revit says \"-01-BF04-PVL\" or \"00-GROUND\"), type the " +
                       "register's wording here. Leave a row blank when both already match.",
                FontSize = 12,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            }, Dock.Top));

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 88, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button
            {
                Content = "Save",
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6),
                IsDefault = true,
                Foreground = Brushes.White,
                Background = Accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            ok.Click += (s, e) => { _confirmed = true; DialogResult = true; };
            buttonBar.Children.Add(cancel);
            buttonBar.Children.Add(ok);
            root.Children.Add(DockedTo(buttonBar, Dock.Bottom));

            var listBorder = new Border { BorderBrush = BorderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            grid.RowDefinitions.Add(new RowDefinition());
            string[] headers = { "Level in Revit", "Written in the register as" };
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = new Border { Background = HeaderBg, Padding = new Thickness(8, 6, 8, 6) };
                cell.Child = new TextBlock { Text = headers[c], FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Ink };
                Grid.SetRow(cell, 0); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>().OrderBy(l => l.Elevation).ToList();

            int rowIndex = 1;
            foreach (Level level in levels)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                Brush bg = (rowIndex % 2 == 0) ? RowAlt : Brushes.Transparent;
                for (int c = 0; c < 2; c++)
                {
                    var cellBg = new Border { Background = bg };
                    Grid.SetRow(cellBg, rowIndex); Grid.SetColumn(cellBg, c);
                    grid.Children.Add(cellBg);
                }

                var name = new TextBlock
                {
                    Text = level.Name,
                    FontSize = 12,
                    Foreground = Ink,
                    Margin = new Thickness(8, 7, 8, 7),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(name, rowIndex); Grid.SetColumn(name, 0);
                grid.Children.Add(name);

                // A value the user already saved always wins over a fresh AI suggestion.
                string existing = null;
                if (settings.LevelAliases != null) settings.LevelAliases.TryGetValue(level.Name, out existing);
                bool fromAi = false;
                if (string.IsNullOrWhiteSpace(existing) && suggestions != null &&
                    suggestions.TryGetValue(level.Name, out string suggested) && !string.IsNullOrWhiteSpace(suggested))
                {
                    existing = suggested;
                    fromAi = true;
                }
                var box = new TextBox
                {
                    Text = existing ?? "",
                    Margin = new Thickness(4),
                    Padding = new Thickness(4, 2, 4, 2),
                    Foreground = Ink,
                    // Suggestions are tinted so it's obvious which rows the AI filled in and need a look.
                    Background = fromAi ? Brush(0xFF, 0xF6, 0xD8) : Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(box, rowIndex); Grid.SetColumn(box, 1);
                grid.Children.Add(box);

                _rows.Add(new AliasRow { LevelName = level.Name, Box = box });
                rowIndex++;
            }

            scroll.Content = grid;
            listBorder.Child = scroll;
            root.Children.Add(listBorder);
            Content = root;
        }

        // Named DockedTo, not Dock — a method called Dock would shadow the WPF Dock enum in this class.
        private static FrameworkElement DockedTo(FrameworkElement el, Dock side)
        {
            DockPanel.SetDock(el, side);
            return el;
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>Shows the mapping editor and, on Save, writes the aliases into 'settings'
        /// (the caller persists). 'suggestions' pre-fills empty rows (highlighted) — used for the
        /// AI-proposed mapping, which the user reviews rather than has silently applied, since a wrong
        /// mapping would put wrong drawing numbers on real sheets. Returns true when something was saved.</summary>
        internal static bool Edit(Document doc, Ar400Settings settings, IntPtr ownerHandle,
            Dictionary<string, string> suggestions = null, string introOverride = null)
        {
            var win = new LevelAliasWindow(doc, settings, ownerHandle, suggestions, introOverride);
            bool? result = win.ShowDialog();
            if (result != true || !win._confirmed) return false;

            settings.LevelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AliasRow row in win._rows)
            {
                string alias = (row.Box.Text ?? "").Trim();
                if (alias.Length > 0) settings.LevelAliases[row.LevelName] = alias;   // blank = match on the level's own name
            }
            return true;
        }
    }
}
