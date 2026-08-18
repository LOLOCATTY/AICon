using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB has its own "Grid" (grid lines) and "Color" (element color overrides) —
// alias the WPF ones explicitly, same pattern as PackageSettingsWindow.cs.
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace AICon
{
    // Mirrors Revit's own "Tag All Not Tagged" dialog (category checkbox + loaded tag type per row,
    // one Leader checkbox, one Tag Orientation) so AR400's per-package tag setup feels familiar.
    // Unlike Revit's native command, AR400 ALSO runs a clash-avoidance pass when it actually places
    // these tags (see ShopDrawings.cs's TagCategory) — this window only decides WHICH categories,
    // which loaded tag type, leader, and orientation to use; not where each tag ends up.
    internal sealed class TagCategoriesWindow : Window
    {
        private sealed class CatRow
        {
            public BuiltInCategory Model;
            public CheckBox Enabled;
            public ComboBox TagType;
        }

        private sealed class TypeOption
        {
            public int? TypeId;
            public string Label;
            public override string ToString() => Label;
        }

        private readonly List<CatRow> _rows = new List<CatRow>();
        private bool _confirmed;
        private List<Ar400TagCategoryProfile> _chosenCategories;
        private bool _chosenLeader;
        private bool _chosenOrientationVertical;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush RowAlt = Brush(0xF4, 0xF6, 0xF8);
        private static readonly Brush HeaderBg = Brush(0xEE, 0xF1, 0xF5);

        private TagCategoriesWindow(Document doc, string packageLabel, List<Ar400TagCategoryProfile> current,
            bool currentLeader, bool currentOrientationVertical, IntPtr ownerHandle)
        {
            var currentByCategory = (current ?? new List<Ar400TagCategoryProfile>())
                .Where(c => c.Category != null)
                .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            Title = "AR400 — Tag categories for " + packageLabel;
            Width = 620;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            CheckListWindow.SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            var header = new TextBlock
            {
                Text = "Select at least one category to tag inside every " + packageLabel + " view. " +
                       "AR400 automatically nudges tags apart if they'd overlap.",
                FontSize = 13,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var leaderBox = new CheckBox
            {
                Content = "Leader",
                FontSize = 12,
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = currentLeader,
                Margin = new Thickness(0, 0, 24, 0)
            };
            optionsPanel.Children.Add(leaderBox);
            optionsPanel.Children.Add(new TextBlock
            {
                Text = "Tag orientation:",
                FontSize = 12,
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var orientationBox = new ComboBox { Width = 110, Height = 24 };
            orientationBox.Items.Add("Horizontal");
            orientationBox.Items.Add("Vertical");
            orientationBox.SelectedIndex = currentOrientationVertical ? 1 : 0;
            optionsPanel.Children.Add(orientationBox);
            DockPanel.SetDock(optionsPanel, Dock.Bottom);
            root.Children.Add(optionsPanel);

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 88, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button
            {
                Content = "OK",
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
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var listBorder = new Border { BorderBrush = BorderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });

            string[] colHeaders = { "", "Category", "Loaded Tag" };
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < colHeaders.Length; c++)
            {
                var cell = new Border { Background = HeaderBg, Padding = new Thickness(8, 6, 8, 6) };
                cell.Child = new TextBlock { Text = colHeaders[c], FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Ink };
                Grid.SetRow(cell, 0); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            List<(BuiltInCategory Model, BuiltInCategory Tag, string Label)> available = TaggableCategories.Available(doc);
            int rowIndex = 1;
            foreach (var def in available)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                Brush bg = (rowIndex % 2 == 0) ? RowAlt : Brushes.Transparent;
                for (int c = 0; c < 3; c++)
                {
                    var cellBg = new Border { Background = bg };
                    Grid.SetRow(cellBg, rowIndex); Grid.SetColumn(cellBg, c);
                    grid.Children.Add(cellBg);
                }

                Ar400TagCategoryProfile existing;
                currentByCategory.TryGetValue(def.Model.ToString(), out existing);

                var enabledBox = new CheckBox { IsChecked = existing != null, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6) };
                Grid.SetRow(enabledBox, rowIndex); Grid.SetColumn(enabledBox, 0);
                grid.Children.Add(enabledBox);

                var label = new TextBlock { Text = def.Label, FontSize = 12, Foreground = Ink, Margin = new Thickness(8, 7, 8, 7), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, rowIndex); Grid.SetColumn(label, 1);
                grid.Children.Add(label);

                var typeOptions = new List<TypeOption> { new TypeOption { TypeId = null, Label = "(project default)" } };
                typeOptions.AddRange(TaggableCategories.LoadedTagTypes(doc, def.Tag)
                    .Select(t => new TypeOption { TypeId = t.TypeId, Label = t.Label }));
                var typeBox = new ComboBox { Margin = new Thickness(4), ItemsSource = typeOptions, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                typeBox.SelectedIndex = existing != null && existing.TagTypeId.HasValue
                    ? Math.Max(0, typeOptions.FindIndex(o => o.TypeId == existing.TagTypeId.Value))
                    : 0;
                Grid.SetRow(typeBox, rowIndex); Grid.SetColumn(typeBox, 2);
                grid.Children.Add(typeBox);

                _rows.Add(new CatRow { Model = def.Model, Enabled = enabledBox, TagType = typeBox });
                rowIndex++;
            }

            scroll.Content = grid;
            listBorder.Child = scroll;
            root.Children.Add(listBorder);
            Content = root;

            Closed += (s, e) =>
            {
                if (!_confirmed) return;
                _chosenCategories = _rows.Where(r => r.Enabled.IsChecked == true)
                    .Select(r =>
                    {
                        var opt = r.TagType.SelectedItem as TypeOption;
                        return new Ar400TagCategoryProfile { Category = r.Model.ToString(), TagTypeId = opt != null ? opt.TypeId : null };
                    }).ToList();
                _chosenLeader = leaderBox.IsChecked == true;
                _chosenOrientationVertical = orientationBox.SelectedIndex == 1;
            };
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>Shows the dialog modally. 'cancelled' is true only on Cancel/X — the caller should
        /// leave the package's existing tag configuration untouched in that case (all out params echo
        /// the CURRENT values back so a naive caller that ignores 'cancelled' still doesn't lose data).</summary>
        internal static List<Ar400TagCategoryProfile> Pick(Document doc, string packageLabel,
            List<Ar400TagCategoryProfile> current, bool currentLeader, bool currentOrientationVertical,
            out bool leader, out bool orientationVertical, out bool cancelled, IntPtr ownerHandle)
        {
            var win = new TagCategoriesWindow(doc, packageLabel, current, currentLeader, currentOrientationVertical, ownerHandle);
            bool? result = win.ShowDialog();
            cancelled = result != true || !win._confirmed;
            leader = cancelled ? currentLeader : win._chosenLeader;
            orientationVertical = cancelled ? currentOrientationVertical : win._chosenOrientationVertical;
            return cancelled ? current : win._chosenCategories;
        }
    }
}
