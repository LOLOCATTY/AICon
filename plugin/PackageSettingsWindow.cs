using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
// Autodesk.Revit.DB has its own "Grid" (grid lines) and Autodesk.Revit.UI its own "ComboBox"/
// "TextBox" (ribbon controls) — alias the WPF ones explicitly; an alias wins over the wildcard usings above.
using Grid = System.Windows.Controls.Grid;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace AICon
{
    // "AR400 Settings" — per-package automation editor (one row per shop-drawing package: view
    // template, room/wall tags, sheet schedule). Code-only WPF, same light theme as the other AICon
    // dialogs. Opened any time from the ribbon; AR400 itself just reads whatever was last saved here.
    [Transaction(TransactionMode.Manual)]
    public sealed class Ar400SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null)
                {
                    TaskDialog.Show("AR400 Settings", "Open a project first.");
                    return Result.Cancelled;
                }
                new PackageSettingsWindow(uidoc.Document, commandData.Application.MainWindowHandle).ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Revit's default is a generic "could not complete the external command" box — show the
                // real cause and log the stack trace instead.
                App.Log("AR400 Settings FAILED: " + ex);
                new TaskDialog("AR400 Settings")
                {
                    MainInstruction = "AR400 Settings could not open",
                    MainContent = ex.Message,
                    FooterText = "Full details: " + App.LogPath
                }.Show();
                return Result.Failed;
            }
        }
    }

    internal sealed class PackageSettingsWindow : Window
    {
        private sealed class Option
        {
            public int? TemplateId;
            public string Value;
            public string Label;
            public override string ToString() => Label;
        }

        private sealed class Row
        {
            public string Package;
            public ComboBox Template;
            public Button TagsButton;
            public List<Ar400TagCategoryProfile> SelectedTagCategories;
            // Read at Save time, not construction time — the Tags button's dialog mutates the
            // underlying local variables in BuildRow's closure whenever the user changes them.
            public Func<bool> Leader;
            public Func<bool> OrientationVertical;
            public ComboBox Dimensions;
            public CheckBox ScheduleEnabled;
            public ComboBox ScheduleCategory;
            public TextBox ScheduleName;
        }

        private static readonly string[] ScheduleCategories = { "Doors", "Windows", "Walls", "Rooms", "Ceilings", "Furniture" };

        private sealed class StripRow
        {
            public ElementId TypeId;
            public TextBox Box;
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<StripRow> _stripRows = new List<StripRow>();
        private readonly Document _doc;
        private readonly IntPtr _ownerHandle;
        private CheckBox _aiToggle;
        private ComboBox _aiProfileBox;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush RowAlt = Brush(0xF4, 0xF6, 0xF8);
        private static readonly Brush HeaderBg = Brush(0xEE, 0xF1, 0xF5);

        public PackageSettingsWindow(Document doc, IntPtr ownerHandle = default)
        {
            _doc = doc;
            _ownerHandle = ownerHandle;
            Title = "AR400 Settings — shop drawing package automation";
            Width = 860;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            CheckListWindow.SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            var header = new TextBlock
            {
                Text = "What AR400 automatically does for each package, every time it creates a view + sheet.",
                FontSize = 13,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var aiPanel = BuildAiDecisionsPanel();
            DockPanel.SetDock(aiPanel, Dock.Top);
            root.Children.Add(aiPanel);

            var stripPanel = BuildTitleBlockStripPanel(Ar400Settings.Load());
            DockPanel.SetDock(stripPanel, Dock.Bottom);
            root.Children.Add(stripPanel);

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            // Opens the level ↔ register-label mapping. Saved immediately (its own dialog has its own
            // Save), so it works whether or not the user then saves this window.
            var levelsButton = new Button
            {
                Content = "Level names in register…",
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            levelsButton.Click += (s, e) =>
            {
                Ar400Settings current = Ar400Settings.Load();
                if (LevelAliasWindow.Edit(_doc, current, _ownerHandle)) current.Save();
            };
            buttonBar.Children.Add(levelsButton);

            var cancel = new Button { Content = "Cancel", MinWidth = 88, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var save = new Button
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
            save.Click += (s, e) => { SaveSettings(); Close(); };
            buttonBar.Children.Add(cancel);
            buttonBar.Children.Add(save);
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var listBorder = new Border { BorderBrush = BorderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };

            var grid = new Grid();
            for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[6].Width = new GridLength(1, GridUnitType.Star);

            string[] headers = { "Package", "View template", "Tags", "Dimensions", "Sched.", "Category", "Schedule name" };
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = new Border { Background = HeaderBg, Padding = new Thickness(8, 6, 8, 6) };
                cell.Child = new TextBlock { Text = headers[c], FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Ink, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(cell, 0); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            Ar400Settings settings = Ar400Settings.Load();
            int rowIndex = 1;
            foreach (string pkg in Ar400Command.Packages)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                Ar400PackageProfile profile = settings.ForPackage(pkg);
                Row row = BuildRow(grid, rowIndex, pkg, profile);
                _rows.Add(row);
                rowIndex++;
            }

            scroll.Content = grid;
            listBorder.Child = scroll;
            root.Children.Add(listBorder);
            Content = root;
        }

        private static string TagsButtonLabel(int count)
        {
            return count == 0 ? "Tags: none ▾" : "Tags: " + count + " ▾";
        }

        private Row BuildRow(Grid grid, int rowIndex, string package, Ar400PackageProfile profile)
        {
            bool ceiling = package.IndexOf("CEILING", StringComparison.OrdinalIgnoreCase) >= 0;
            Brush bg = (rowIndex % 2 == 0) ? RowAlt : Brushes.Transparent;
            for (int c = 0; c < 7; c++)
            {
                var cellBg = new Border { Background = bg };
                Grid.SetRow(cellBg, rowIndex); Grid.SetColumn(cellBg, c);
                grid.Children.Add(cellBg);
            }

            var name = new TextBlock { Text = package, FontSize = 12, Foreground = Ink, Margin = new Thickness(8, 8, 8, 8), VerticalAlignment = VerticalAlignment.Center };
            Place(grid, name, rowIndex, 0);

            var templateOptions = new List<Option> { new Option { TemplateId = null, Label = "(auto by name)" } };
            ViewType wantType = ceiling ? ViewType.CeilingPlan : ViewType.FloorPlan;
            templateOptions.AddRange(
                new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate && v.ViewType == wantType)
                    .OrderBy(v => v.Name)
                    .Select(v => new Option { TemplateId = v.Id.IntegerValue, Label = v.Name }));
            var templateBox = new ComboBox { Margin = new Thickness(4), ItemsSource = templateOptions, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            templateBox.SelectedIndex = profile.ViewTemplateId.HasValue
                ? Math.Max(0, templateOptions.FindIndex(o => o.TemplateId == profile.ViewTemplateId.Value))
                : 0;
            Place(grid, templateBox, rowIndex, 1);

            List<Ar400TagCategoryProfile> selected = new List<Ar400TagCategoryProfile>(profile.TagCategories ?? new List<Ar400TagCategoryProfile>());
            bool leader = profile.TagLeader;
            bool orientationVertical = profile.TagOrientationVertical;
            var tagsButton = new Button
            {
                Content = TagsButtonLabel(selected.Count),
                Margin = new Thickness(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            tagsButton.Click += (s, e) =>
            {
                if (TaggableCategories.Available(_doc).Count == 0)
                {
                    TaskDialog.Show("AR400 Settings",
                        "No taggable categories found in this project yet — place some elements of a category " +
                        "and make sure its tag family is loaded, then reopen AR400 Settings.");
                    return;
                }
                bool cancelled;
                List<Ar400TagCategoryProfile> picked = TagCategoriesWindow.Pick(
                    _doc, package, selected, leader, orientationVertical,
                    out leader, out orientationVertical, out cancelled, _ownerHandle);
                if (cancelled) return;   // leave the existing selection untouched
                selected.Clear();
                selected.AddRange(picked);
                tagsButton.Content = TagsButtonLabel(selected.Count);
            };
            Place(grid, tagsButton, rowIndex, 2);

            // What AR400 dimensions inside each view of this package. Grid strings are the safe,
            // always-useful default; wall pairs give the real face-to-face room widths.
            var dimOptions = new List<Option>
            {
                new Option { Value = "none",        Label = "None" },
                new Option { Value = "grids",       Label = "Grid strings" },
                new Option { Value = "walls",       Label = "Wall faces" },
                new Option { Value = "grids+walls", Label = "Grids + walls" },
            };
            var dimBox = new ComboBox { Margin = new Thickness(4), ItemsSource = dimOptions, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            string curDim = string.IsNullOrWhiteSpace(profile.Dimensions) ? "none" : profile.Dimensions.Trim().ToLowerInvariant();
            dimBox.SelectedIndex = Math.Max(0, dimOptions.FindIndex(o => o.Value == curDim));
            Place(grid, dimBox, rowIndex, 3);

            var schedEnabled = new CheckBox { IsChecked = profile.Schedule != null && profile.Schedule.Enabled, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4) };
            Place(grid, schedEnabled, rowIndex, 4);

            var catOptions = ScheduleCategories.Select(c => new Option { Value = c, Label = c }).ToList();
            var schedCategory = new ComboBox { Margin = new Thickness(4), ItemsSource = catOptions, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            string curCat = profile.Schedule != null && !string.IsNullOrEmpty(profile.Schedule.Category) ? profile.Schedule.Category : "Doors";
            schedCategory.SelectedIndex = Math.Max(0, catOptions.FindIndex(o => string.Equals(o.Value, curCat, StringComparison.OrdinalIgnoreCase)));
            Place(grid, schedCategory, rowIndex, 5);

            var schedName = new TextBox
            {
                Text = profile.Schedule != null && !string.IsNullOrEmpty(profile.Schedule.Name) ? profile.Schedule.Name : (package + " Schedule"),
                Margin = new Thickness(4),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ink
            };
            Place(grid, schedName, rowIndex, 6);

            return new Row
            {
                Package = package,
                Template = templateBox,
                TagsButton = tagsButton,
                SelectedTagCategories = selected,
                Leader = () => leader,
                OrientationVertical = () => orientationVertical,
                Dimensions = dimBox,
                ScheduleEnabled = schedEnabled,
                ScheduleCategory = schedCategory,
                ScheduleName = schedName
            };
        }

        private static void Place(Grid grid, FrameworkElement el, int row, int col)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col);
            grid.Children.Add(el);
        }

        private void SaveSettings()
        {
            var settings = new Ar400Settings();
            foreach (Row row in _rows)
            {
                var opt = row.Template.SelectedItem as Option;
                var catOpt = row.ScheduleCategory.SelectedItem as Option;
                settings.Packages[row.Package] = new Ar400PackageProfile
                {
                    ViewTemplateId = opt != null ? opt.TemplateId : null,
                    TagCategories = new List<Ar400TagCategoryProfile>(row.SelectedTagCategories ?? new List<Ar400TagCategoryProfile>()),
                    TagLeader = row.Leader(),
                    TagOrientationVertical = row.OrientationVertical(),
                    Dimensions = (row.Dimensions.SelectedItem as Option) != null
                        ? ((Option)row.Dimensions.SelectedItem).Value : "none",
                    Schedule = new Ar400ScheduleProfile
                    {
                        Enabled = row.ScheduleEnabled.IsChecked == true,
                        Category = catOpt != null ? catOpt.Value : "Doors",
                        Name = string.IsNullOrWhiteSpace(row.ScheduleName.Text) ? (row.Package + " Schedule") : row.ScheduleName.Text.Trim()
                    }
                };
            }
            foreach (StripRow strip in _stripRows)
            {
                double mm;
                string text = (strip.Box.Text ?? "").Trim();
                // Blank (or unparseable) means "keep detecting automatically" — store nothing.
                if (text.Length > 0 && double.TryParse(text, out mm) && mm > 0)
                    settings.TitleBlockStripMm[strip.TypeId.IntegerValue.ToString()] = mm;
            }
            settings.Save();

            AIConAiConfig aiCfg = AIConAiConfig.Load();
            aiCfg.UseAiDecisions = _aiToggle.IsChecked == true;
            var askOpt = _aiProfileBox.SelectedItem as Option;
            aiCfg.UseProfile = askOpt != null ? askOpt.Value : "";
            aiCfg.Save();
        }

        // "Title block" section: the width of the right-hand data strip (خرطوشة) that AR400 must keep
        // views and schedules clear of. AR400 detects this from the title block's geometry; this row
        // exists so a wrong guess can be corrected once and remembered. Blank/0 = keep auto-detecting.
        private Border BuildTitleBlockStripPanel(Ar400Settings settings)
        {
            var stack = new StackPanel { Margin = new Thickness(10) };
            stack.Children.Add(new TextBlock
            {
                Text = "Title block — width of the right-hand data strip that content must avoid",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                Margin = new Thickness(0, 0, 0, 2)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Leave blank to detect it automatically from the title block. Set a value in mm only if the automatic result is wrong.",
                FontSize = 11,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var types = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().Cast<FamilySymbol>()
                .OrderBy(s => s.Family.Name).ThenBy(s => s.Name).ToList();

            if (types.Count == 0)
                stack.Children.Add(new TextBlock { Text = "No title block families are loaded in this project.", FontSize = 11, Foreground = Muted });

            foreach (FamilySymbol type in types)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = type.Family.Name + " : " + type.Name,
                    FontSize = 12,
                    Foreground = Ink,
                    Width = 460,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                double saved = settings.StripOverrideMm(type.Id.IntegerValue);
                var box = new TextBox
                {
                    Text = saved > 0 ? saved.ToString("0.#") : "",
                    Width = 80,
                    Padding = new Thickness(4, 2, 4, 2),
                    Foreground = Ink,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(box);
                row.Children.Add(new TextBlock
                {
                    Text = "  mm",
                    FontSize = 11,
                    Foreground = Muted,
                    VerticalAlignment = VerticalAlignment.Center
                });
                stack.Children.Add(row);
                _stripRows.Add(new StripRow { TypeId = type.Id, Box = box });
            }

            return new Border
            {
                Background = HeaderBg,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 12, 0, 0),
                Child = stack
            };
        }

        // "AI Decisions" section: the master on/off toggle (AR400 still works fully deterministically
        // with this off, or with no API key set — see AIConDecisionClient.cs) plus a read-only status
        // line. No network call here — GetStatus() only reports what's saved in ar400ai.json.
        private Border BuildAiDecisionsPanel()
        {
            AIConDecisionClient.StatusInfo status = AIConDecisionClient.GetStatus();

            var stack = new StackPanel { Margin = new Thickness(10) };
            _aiToggle = new CheckBox
            {
                Content = "Use AI-assisted decisions (Excel column mapping, tag placement, dimension face selection)",
                IsChecked = status.Enabled,
                FontSize = 12,
                Foreground = Ink,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(_aiToggle);

            // These are small, rare questions, so they can run on a different agent than the chat —
            // e.g. Gemini's free tier or the offline local model, even while chatting on DeepSeek.
            var askRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            askRow.Children.Add(new TextBlock
            {
                Text = "Ask:",
                FontSize = 12,
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var askOptions = new List<Option>
            {
                new Option { Value = "",         Label = "Same agent as the AI Chat" },
                new Option { Value = "gemini",   Label = "Gemini  (free tier)" },
                new Option { Value = "deepseek", Label = "DeepSeek" },
                new Option { Value = "local",    Label = "Local model  (free, offline)" },
            };
            _aiProfileBox = new ComboBox { ItemsSource = askOptions, Width = 240, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            string savedProfile = AIConAiConfig.Load().UseProfile ?? "";
            _aiProfileBox.SelectedIndex = Math.Max(0, askOptions.FindIndex(o => string.Equals(o.Value, savedProfile, StringComparison.OrdinalIgnoreCase)));
            askRow.Children.Add(_aiProfileBox);
            stack.Children.Add(askRow);

            string statusText = status.Configured
                ? "Using: " + status.Provider + " / " + status.Model +
                  "  —  the same AI as the AICon chat panel. To use a different model just for these " +
                  "decisions, set one in " + AIConAiConfig.FilePath
                : "No AI is set up yet, so AR400 stays fully deterministic and asks you when something is " +
                  "ambiguous. To enable, pick an agent on the AICon ribbon (Gemini / DeepSeek / Local) and " +
                  "enter its key once — AR400 then reuses it.";
            stack.Children.Add(new TextBlock
            {
                Text = statusText,
                FontSize = 11,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = HeaderBg,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 12),
                Child = stack
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
