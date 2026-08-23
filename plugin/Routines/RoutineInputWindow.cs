using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Grid = System.Windows.Controls.Grid;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace AICon.Routines
{
    // Builds the routine's dialog FROM its declared inputs — there is deliberately no hand-written
    // window per routine. The same `inputs` list also becomes the MCP input schema, so the form the
    // user sees and the arguments the AI sends can never drift apart.
    internal sealed class RoutineInputWindow : Window
    {
        private sealed class FieldRow
        {
            public RoutineInput Input;
            public Func<object> Read;      // pulls the current value out of whatever control was built
        }

        private readonly List<FieldRow> _fields = new List<FieldRow>();
        private bool _confirmed;

        private static readonly Brush Ink = Brush(0x1A, 0x1A, 0x1A);
        private static readonly Brush Muted = Brush(0x6B, 0x70, 0x76);
        private static readonly Brush Accent = Brush(0x2E, 0x7D, 0xD1);
        private static readonly Brush BorderClr = Brush(0xC7, 0xCC, 0xD2);
        private static readonly Brush WarnBg = Brush(0xFF, 0xF4, 0xE5);

        private RoutineInputWindow(Routine routine, UIDocument uidoc, IntPtr ownerHandle)
        {
            Title = routine.Name;
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            CheckListWindow.SetOwnerOrTopmost(this, ownerHandle);

            var root = new DockPanel { Margin = new Thickness(20) };

            if (!string.IsNullOrWhiteSpace(routine.Description))
            {
                var desc = new TextBlock
                {
                    Text = routine.Description,
                    FontSize = 12,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                DockPanel.SetDock(desc, Dock.Top);
                root.Children.Add(desc);
            }

            // A destructive routine says so before it runs, not after.
            if (routine.Destructive)
            {
                var warn = new Border
                {
                    Background = WarnBg,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock
                    {
                        Text = "This routine deletes or overwrites things. One Ctrl+Z undoes the whole run.",
                        FontSize = 12,
                        Foreground = Ink,
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                DockPanel.SetDock(warn, Dock.Top);
                root.Children.Add(warn);
            }

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 88, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var run = new Button
            {
                Content = "Run",
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6),
                IsDefault = true,
                Foreground = Brushes.White,
                Background = Accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            run.Click += (s, e) => { _confirmed = true; DialogResult = true; };
            buttonBar.Children.Add(cancel);
            buttonBar.Children.Add(run);
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 };
            var stack = new StackPanel();

            if (routine.Inputs == null || routine.Inputs.Count == 0)
                stack.Children.Add(new TextBlock
                {
                    Text = "This routine takes no inputs — press Run.",
                    FontSize = 12,
                    Foreground = Muted
                });
            else
                foreach (RoutineInput inp in routine.Inputs)
                    stack.Children.Add(BuildField(inp, uidoc));

            scroll.Content = stack;
            root.Children.Add(scroll);
            Content = root;
        }

        private UIElement BuildField(RoutineInput inp, UIDocument uidoc)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            block.Children.Add(new TextBlock
            {
                Text = inp.DisplayLabel + (inp.Required ? " *" : ""),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                Margin = new Thickness(0, 0, 0, 3)
            });

            string type = (inp.Type ?? RoutineInputType.String).Trim();
            switch (type)
            {
                case RoutineInputType.Boolean:
                {
                    var box = new CheckBox
                    {
                        Content = inp.Description ?? "",
                        IsChecked = ToBool(inp.Default),
                        FontSize = 12,
                        Foreground = Ink
                    };
                    block.Children.Add(box);
                    _fields.Add(new FieldRow { Input = inp, Read = () => box.IsChecked == true });
                    return block;   // the checkbox carries its own description
                }

                case RoutineInputType.Enum:
                {
                    // 'source' (live from the model) wins over a fixed 'options' list when both are
                    // present; falls back to an empty list (never a live document) rather than guessing.
                    List<string> choices = !string.IsNullOrWhiteSpace(inp.Source)
                        ? ResolveDynamicOptions(inp.Source, uidoc != null ? uidoc.Document : null)
                        : (inp.Options ?? new List<string>());

                    if (inp.Multi)
                    {
                        List<string> preChecked = ToStringList(inp.Default);
                        var checks = new List<CheckBox>();
                        var listPanel = new StackPanel();
                        foreach (string choice in choices)
                        {
                            var cb = new CheckBox
                            {
                                Content = choice,
                                FontSize = 12,
                                Foreground = Ink,
                                Margin = new Thickness(0, 2, 0, 2),
                                IsChecked = preChecked.Any(p => string.Equals(p, choice, StringComparison.OrdinalIgnoreCase))
                            };
                            checks.Add(cb);
                            listPanel.Children.Add(cb);
                        }
                        block.Children.Add(new Border
                        {
                            BorderBrush = BorderClr,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            MaxHeight = 140,
                            Child = new ScrollViewer
                            {
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                Padding = new Thickness(8, 6, 8, 6),
                                Content = listPanel
                            }
                        });
                        if (choices.Count == 0)
                            block.Children.Add(Hint(!string.IsNullOrWhiteSpace(inp.Source)
                                ? "Nothing found in the model for this."
                                : "No options configured for this input."));
                        _fields.Add(new FieldRow
                        {
                            Input = inp,
                            Read = () => checks.Where(c => c.IsChecked == true)
                                .Select(c => (object)Convert.ToString(c.Content, CultureInfo.InvariantCulture)).ToList()
                        });
                        break;
                    }

                    var combo = new ComboBox { Height = 24, ItemsSource = choices };
                    string def = inp.Default == null ? null : Convert.ToString(inp.Default, CultureInfo.InvariantCulture);
                    combo.SelectedIndex = def != null
                        ? Math.Max(0, choices.FindIndex(o => string.Equals(o, def, StringComparison.OrdinalIgnoreCase)))
                        : (choices.Count > 0 ? 0 : -1);
                    block.Children.Add(combo);
                    _fields.Add(new FieldRow { Input = inp, Read = () => combo.SelectedItem as string });
                    break;
                }

                case RoutineInputType.StringArray:
                {
                    var box = NewTextBox(inp.Default == null ? "" : string.Join(Environment.NewLine, ToStringList(inp.Default)));
                    box.AcceptsReturn = true;
                    box.TextWrapping = TextWrapping.Wrap;
                    box.Height = 72;
                    box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    block.Children.Add(box);
                    block.Children.Add(Hint("One per line."));
                    _fields.Add(new FieldRow
                    {
                        Input = inp,
                        Read = () => (box.Text ?? "")
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim()).Where(x => x.Length > 0).Cast<object>().ToList()
                    });
                    break;
                }

                case RoutineInputType.ElementId:
                {
                    // Typing an element id is hostile; offer to pick it in the model instead.
                    var row = new DockPanel();
                    var pick = new Button
                    {
                        Content = "Pick…",
                        Padding = new Thickness(10, 3, 10, 3),
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    DockPanel.SetDock(pick, Dock.Right);
                    var box = NewTextBox(inp.Default == null ? "" : Convert.ToString(inp.Default, CultureInfo.InvariantCulture));
                    row.Children.Add(pick);
                    row.Children.Add(box);
                    block.Children.Add(row);

                    pick.Click += (s, e) =>
                    {
                        if (uidoc == null) return;
                        try
                        {
                            // Hide this dialog while picking, or it sits on top of the model.
                            Hide();
                            Reference picked = uidoc.Selection.PickObject(ObjectType.Element, "Pick the element for '" + inp.DisplayLabel + "'");
                            if (picked != null) box.Text = picked.ElementId.ToInt().ToString(CultureInfo.InvariantCulture);
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* user pressed Esc — normal */ }
                        catch (Exception ex) { TaskDialog.Show("AICon", "Could not pick: " + ex.Message); }
                        finally { ShowDialog0(); }
                    };

                    _fields.Add(new FieldRow { Input = inp, Read = () => ParseNumber(box.Text) });
                    break;
                }

                case RoutineInputType.Number:
                case RoutineInputType.Integer:
                {
                    var box = NewTextBox(inp.Default == null ? "" : Convert.ToString(inp.Default, CultureInfo.InvariantCulture));
                    block.Children.Add(box);
                    _fields.Add(new FieldRow { Input = inp, Read = () => ParseNumber(box.Text) });
                    break;
                }

                default:
                {
                    var box = NewTextBox(inp.Default == null ? "" : Convert.ToString(inp.Default, CultureInfo.InvariantCulture));
                    block.Children.Add(box);
                    _fields.Add(new FieldRow { Input = inp, Read = () => box.Text });
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(inp.Description)) block.Children.Add(Hint(inp.Description));
            return block;
        }

        // Re-showing a modal after PickObject: ShowDialog() can't be called twice on the same Window,
        // so the dialog is only hidden and brought back with Show() — it stays modal to Revit because
        // its Owner is still Revit's main window.
        private void ShowDialog0() { try { Show(); Activate(); } catch { } }

        private static TextBox NewTextBox(string text) => new TextBox
        {
            Text = text ?? "",
            FontSize = 13,
            Padding = new Thickness(5, 3, 5, 3),
            Foreground = Ink,
            BorderBrush = BorderClr
        };

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };

        // Deliberately mirrors the shape of the matching read tools (list_levels/list_views/list_sheets/
        // list_categories) closely enough that a routine author's mental model of "what would this
        // return" carries over, without actually calling through ToolDispatcher (this runs while the
        // form is open, not inside a tool dispatch, and only needs names, not full records).
        private static List<string> ResolveDynamicOptions(string source, Document doc)
        {
            if (doc == null) return new List<string>();
            switch ((source ?? "").Trim().ToLowerInvariant())
            {
                case RoutineInputSource.Levels:
                    return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
                case RoutineInputSource.Views:
                    return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .OrderBy(v => v.Name).Select(v => v.Name).ToList();
                case RoutineInputSource.Sheets:
                    return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .OrderBy(s => s.SheetNumber).Select(s => s.SheetNumber + " - " + s.Name).ToList();
                case RoutineInputSource.Categories:
                    var names = new HashSet<string>();
                    foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                        if (e.Category != null) names.Add(e.Category.Name);
                    return names.OrderBy(n => n).ToList();
                case RoutineInputSource.Worksets:
                    // User worksets only — not the fixed system ones (Shared Levels and Grids etc. are
                    // still real worksets and would appear here too if OfKind were left off, which is
                    // usually not what an author wants to offer as a destination). Empty (not an error)
                    // on a non-workshared model — doc.IsWorkshared is false there.
                    if (!doc.IsWorkshared) return new List<string>();
                    return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                        .OrderBy(w => w.Name).Select(w => w.Name).ToList();
                default:
                    return new List<string>();
            }
        }

        private static object ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            double d;
            if (double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return text.Trim();   // let the tool complain about it with its own message
        }

        private static bool ToBool(object v)
        {
            if (v is bool b) return b;
            bool parsed;
            return v != null && bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) && parsed;
        }

        private static List<string> ToStringList(object v)
        {
            var list = v as System.Collections.IEnumerable;
            var result = new List<string>();
            if (list != null && !(v is string))
                foreach (object o in list) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            else if (v != null) result.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
            return result;
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>Shows the generated form. Returns null if the user cancelled.</summary>
        internal static Dictionary<string, object> Prompt(Routine routine, UIDocument uidoc, IntPtr ownerHandle)
        {
            var win = new RoutineInputWindow(routine, uidoc, ownerHandle);
            bool? ok = win.ShowDialog();
            if (ok != true || !win._confirmed) return null;

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldRow f in win._fields)
            {
                object v = null;
                try { v = f.Read(); } catch { }
                if (v != null) values[f.Input.Name] = v;
            }
            return values;
        }
    }
}
