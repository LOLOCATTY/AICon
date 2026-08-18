using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace AICon
{
    // ============================================================================
    //  AR400 — architectural shop drawing starter.
    //  Ribbon button → package(s) → level(s) → scope box (optional) → title block →
    //  sheet register Excel (optional) → for every package×level combo AR400 creates
    //  a plan view, applies a view template (per-package setting, else name-match
    //  fallback) and scope box crop, creates a sheet (number/name from Excel when
    //  matched), places the view on it, then runs that package's saved automation
    //  (AR400 Settings): room/wall tags in the view, an openings-style schedule on
    //  the sheet. Everything happens in ONE transaction (a single Ctrl+Z undoes it).
    //  Re-running never duplicates: views/sheets/schedules whose name is already
    //  taken are reused or skipped, never recreated.
    // ============================================================================
    [Transaction(TransactionMode.Manual)]
    public class Ar400Command : IExternalCommand
    {
        // The packages offered in the first window — edit this list freely.
        // Any entry containing "CEILING" is created as a ceiling plan.
        internal static readonly string[] Packages =
        {
            "GENERAL ARRANGEMENT",
            "BLOCKWORK",
            "FLOORING",
            "CEILING",
            "WALL FINISHES",
            "DOORS",
            "WET AREAS",
            "SETTING OUT",
        };

        // Revit's own handler for an escaped exception is a generic "Revit could not complete the
        // external command" box with no cause, which is undiagnosable. Catch everything here, write the
        // full stack trace to the AICon log, and show the actual message.
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { return Run(commandData); }
            catch (Exception ex)
            {
                App.Log("AR400 FAILED: " + ex);
                new TaskDialog("AR400")
                {
                    MainInstruction = "AR400 could not finish",
                    MainContent = ex.Message + "\n\nNothing was left half-done — the whole run was rolled back.",
                    FooterText = "Full details: " + App.LogPath
                }.Show();
                return Result.Failed;   // Revit rolls back any transaction still open
            }
        }

        private Result Run(ExternalCommandData commandData)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                TaskDialog.Show("AR400", "Open a project first.");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            // Guard the document states where AR400 cannot work, BEFORE asking the user anything —
            // walking them through five dialogs only to fail at the transaction would be worse.
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("AR400", "AR400 builds project sheets — it can't run in the Family Editor. " +
                                          "Open the project instead.");
                return Result.Cancelled;
            }
            if (doc.IsReadOnly)
            {
                TaskDialog.Show("AR400", "This document is read-only, so no views or sheets can be created.");
                return Result.Cancelled;
            }

            IntPtr ownerHandle = commandData.Application.MainWindowHandle;

            // ---- 1) packages ----
            bool packagesCancelled;
            List<string> packages = CheckListWindow.Pick(
                "AR400 — Shop Drawings", "Select the shop drawing package(s):", Packages, "Next", out packagesCancelled, ownerHandle);
            if (packagesCancelled || packages == null || packages.Count == 0) return Result.Cancelled;

            // ---- 2) levels ----
            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>().OrderBy(l => l.Elevation).ToList();
            if (levels.Count == 0) { TaskDialog.Show("AR400", "This project has no levels."); return Result.Cancelled; }

            bool levelsCancelled;
            List<string> pickedNames = CheckListWindow.Pick(
                "AR400 — Shop Drawings", "Select the level(s):", levels.Select(l => l.Name), "Next", out levelsCancelled, ownerHandle);
            if (levelsCancelled || pickedNames == null || pickedNames.Count == 0) return Result.Cancelled;
            var wanted = new HashSet<string>(pickedNames, StringComparer.OrdinalIgnoreCase);
            List<Level> pickedLevels = levels.Where(l => wanted.Contains(l.Name)).ToList();

            // ---- 3) scope box (optional — only asked when the project has any) ----
            Element scopeBox = null;
            var scopeBoxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType().OrderBy(e => e.Name).ToList();
            if (scopeBoxes.Count > 0)
            {
                var options = scopeBoxes.Select(e => (e.Id.IntegerValue.ToString(), e.Name));
                bool cancelled;
                string chosen = CheckListWindow.PickOne(
                    "AR400 — Shop Drawings", "Crop these views to a scope box? (optional)",
                    options, "(None — no crop)", "Next", out cancelled, ownerHandle);
                if (cancelled) return Result.Cancelled;   // X/Cancel = stop everything, not "no crop, keep going"
                if (chosen != null) scopeBox = doc.GetElement(new ElementId(int.Parse(chosen)));
            }

            // ---- 4) title block ----
            ElementId titleBlockId = ElementId.InvalidElementId;
            var titleBlockTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().OrderBy(e => e.Name).ToList();
            if (titleBlockTypes.Count > 0)
            {
                var options = titleBlockTypes.Select(e =>
                {
                    var fam = e as FamilySymbol;
                    string label = fam != null ? fam.Family.Name + " : " + e.Name : e.Name;
                    return (e.Id.IntegerValue.ToString(), label);
                });
                bool cancelled;
                string chosen = CheckListWindow.PickOne(
                    "AR400 — Shop Drawings", "Select the sheets' title block:", options, "(Project default)", "Next", out cancelled, ownerHandle);
                if (cancelled) return Result.Cancelled;   // X/Cancel = stop everything, not "project default, keep going"
                if (chosen != null) titleBlockId = new ElementId(int.Parse(chosen));
            }

            // ---- 5) sheet register (Excel, optional) ----
            List<RegisterRow> register = null;
            var openDlg = new OpenFileDialog
            {
                Title = "AR400 — pick the sheet register (optional, Cancel to skip)",
                Filter = "Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
                CheckFileExists = true
            };
            if (openDlg.ShowDialog() == true)
            {
                string problem;
                register = ReadRegister(openDlg.FileName, out problem);
                if (register == null || register.Count == 0)
                {
                    TaskDialog.Show("AR400", problem + "\n\nContinuing without the sheet register — sheets will be auto-numbered.");
                    register = null;
                }
            }

            Ar400Settings settings = Ar400Settings.Load();

            // ---- 5b) reconcile level naming between the model and the register ----
            // The model and the register frequently name levels differently ("-01-BF04-PVL" vs "BS04"),
            // with no textual overlap for any rule to latch onto — the definition of an ambiguous
            // decision, so this is where the AI decision layer earns its place. It proposes the whole
            // mapping; the user confirms it in the normal alias editor (a wrong mapping would print
            // wrong drawing numbers on real sheets, so it is never applied silently), and the answer is
            // saved so this only ever happens once.
            if (register != null)
            {
                List<string> unmatched = pickedLevels
                    .Where(l => !packages.Any(p => FindRegisterRow(register, p, l.Name, settings) != null))
                    .Select(l => l.Name).ToList();

                if (unmatched.Count > 0)
                {
                    List<string> titles = register.Select(r => r.Name ?? r.Number)
                        .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(80).ToList();

                    string aiUnavailable;
                    var proposal = AIConDecisionClient.DecideLevelMapping(unmatched, titles, out aiUnavailable);
                    var suggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (proposal != null)
                        foreach (KeyValuePair<string, string> kv in proposal.Map)
                        {
                            // Only keep a suggestion that actually occurs in the register — the model is
                            // told not to invent labels, but verify rather than trust.
                            string squashed = Squash(kv.Value);
                            if (squashed.Length > 0 && register.Any(r => r.Haystack.Contains(squashed)))
                                suggestions[kv.Key] = kv.Value;
                        }

                    string intro;
                    if (suggestions.Count > 0)
                        intro = "These levels aren't named the same way in your register, so AR400 worked out the " +
                                "matching for you — the highlighted rows are its suggestions. Check them, fix anything " +
                                "wrong, then Save. You'll only be asked once.";
                    else
                        // Say WHY the rows are empty. Without this the dialog looks identical whether the AI
                        // declined, wasn't set up, or simply couldn't be reached — and the user is left guessing.
                        intro = "These levels aren't named the same way in your register (e.g. it says \"BS04\" where " +
                                "the model says \"-01-BF04-PVL\"). Type the register's wording next to each level you " +
                                "need — you'll only be asked once." +
                                (aiUnavailable != null ? "\n\nAR400 tried to fill these in automatically first. " + aiUnavailable : "");

                    if (LevelAliasWindow.Edit(doc, settings, ownerHandle, suggestions, intro))
                    {
                        settings.Save();
                        settings = Ar400Settings.Load();
                    }
                }
            }

            // Names already used by real (non-template) views — re-runs skip, never duplicate.
            var takenViews = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate).Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            var excelUnmatched = new List<string>();
            var tagWarnings = new List<string>();
            var packagesWithoutTagConfig = new HashSet<string>();
            int templated = 0, tagsPlaced = 0, tagsMoved = 0, schedulesPlaced = 0, dimensionsPlaced = 0;

            // AR400 runs in TWO phases inside one TransactionGroup:
            //   phase 1 (own transaction) — create every view + sheet + viewport, apply templates;
            //   phase 2 (own transaction) — tag and schedule them.
            // The split is REQUIRED, not cosmetic: a view created inside a still-open transaction has
            // not been generated by Revit yet, so asking "what's inside this view?" returns NOTHING and
            // no tags get placed. Committing phase 1 first makes Revit realise the views (with their
            // templates applied) so phase 2 can actually see their contents.
            // TransactionGroup.Assimilate() then merges both into ONE undo step, so the user still
            // presses Ctrl+Z exactly once.
            var built = new List<BuiltSheet>();

            using (var group = new TransactionGroup(doc, "AR400 shop drawing set"))
            {
                group.Start();

                // ---------- phase 1: views, sheets, viewports ----------
                using (var txBuild = new Transaction(doc, "AR400 views & sheets"))
                {
                    txBuild.Start();
                    foreach (string pkg in packages)
                    {
                        bool ceiling = pkg.IndexOf("CEILING", StringComparison.OrdinalIgnoreCase) >= 0;
                        ViewFamily family = ceiling ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
                        ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == family);
                        if (vft == null)
                        {
                            failed.Add(pkg + " — no " + family + " view type exists in this project");
                            continue;
                        }
                        View nameTemplate = FindTemplate(doc, pkg, ceiling);
                        Ar400PackageProfile profile = settings.ForPackage(pkg);

                        foreach (Level level in pickedLevels)
                        {
                            string viewName = "AR400 - " + pkg + " - " + level.Name;
                            if (takenViews.Contains(viewName)) { skipped.Add(viewName); continue; }

                            try
                            {
                                // --- view ---
                                ViewPlan plan = ViewPlan.Create(doc, vft.Id, level.Id);
                                try { plan.Name = viewName; } catch { /* name clash → keep Revit's default */ }
                                takenViews.Add(plan.Name);

                                View template = profile.ViewTemplateId.HasValue
                                    ? doc.GetElement(new ElementId(profile.ViewTemplateId.Value)) as View
                                    : nameTemplate;
                                if (template != null)
                                {
                                    try { plan.ViewTemplateId = template.Id; templated++; }
                                    catch (Exception ex)
                                    {
                                        App.Log("AR400 view template on " + viewName + " failed: " + ex);
                                        tagWarnings.Add(viewName + ": view template \"" + template.Name + "\" could not be applied — " + ex.Message);
                                    }
                                }

                                if (scopeBox != null)
                                {
                                    Parameter sb = plan.LookupParameter("Scope Box");
                                    if (sb != null && !sb.IsReadOnly)
                                    {
                                        try { sb.Set(scopeBox.Id); }
                                        catch (Exception ex)
                                        {
                                            App.Log("AR400 scope box on " + viewName + " failed: " + ex);
                                            tagWarnings.Add(viewName + ": scope box could not be set — " + ex.Message);
                                        }
                                    }
                                }

                                // --- sheet + placement ---
                                RegisterRow entry = FindRegisterRow(register, pkg, level.Name, settings);
                                if (register != null && entry == null) excelUnmatched.Add(viewName);

                                var sheetArgs = new Dictionary<string, object>
                                {
                                    ["name"] = entry != null && !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : viewName
                                };
                                if (entry != null && !string.IsNullOrWhiteSpace(entry.Number)) sheetArgs["number"] = entry.Number;
                                if (titleBlockId != ElementId.InvalidElementId) sheetArgs["title_block_type_id"] = titleBlockId.IntegerValue;

                                var sheetResult = (Dictionary<string, object>)ToolDispatcher.CreateSheet(doc, sheetArgs);
                                var sheet = doc.GetElement(new ElementId(Convert.ToInt32(sheetResult["id"]))) as ViewSheet;

                                ToolDispatcher.PlaceViewOnSheet(doc, new Dictionary<string, object>
                                {
                                    ["view_id"] = plan.Id.IntegerValue,
                                    ["sheet_id"] = sheet.Id.IntegerValue
                                });

                                built.Add(new BuiltSheet
                                {
                                    Package = pkg,
                                    Profile = profile,
                                    Level = level,
                                    ViewId = plan.Id,
                                    SheetId = sheet.Id,
                                    ViewName = viewName
                                });
                                created.Add(viewName);
                            }
                            catch (Exception ex) { failed.Add(viewName + " — " + ex.Message); }
                        }
                    }

                    if (created.Count > 0) txBuild.Commit();
                    else txBuild.RollBack();
                }

                // ---------- phase 2: tags & schedules ----------
                // Views from phase 1 are now committed, so Revit has actually generated them and their
                // contents are visible to view-scoped collectors.
                if (built.Count > 0)
                {
                    using (var txAnnotate = new Transaction(doc, "AR400 tags & schedules"))
                    {
                        txAnnotate.Start();
                        foreach (BuiltSheet item in built)
                        {
                            var plan = doc.GetElement(item.ViewId) as View;
                            var sheet = doc.GetElement(item.SheetId) as ViewSheet;
                            if (plan == null || sheet == null) continue;

                            // One occupied-box list per VIEW: tags only clash with others in the same view.
                            var occupied = new List<BoundingBoxXYZ>();
                            if (item.Profile.TagCategories == null || item.Profile.TagCategories.Count == 0)
                                packagesWithoutTagConfig.Add(item.Package);
                            else
                                foreach (Ar400TagCategoryProfile tagProfile in item.Profile.TagCategories)
                                {
                                    BuiltInCategory bic;
                                    if (tagProfile == null || !Enum.TryParse(tagProfile.Category, out bic)) continue;
                                    // One bad category must not abort the run — the views and sheets are
                                    // already worth keeping by this point.
                                    try
                                    {
                                        TagResult tr = TagCategory(doc, plan, bic, tagProfile,
                                            item.Profile.TagLeader, item.Profile.TagOrientationVertical, occupied);
                                        tagsPlaced += tr.Tagged;
                                        tagsMoved += tr.Moved;
                                        if (tr.Note != null) tagWarnings.Add(item.ViewName + ": " + tr.Note);
                                    }
                                    catch (Exception ex)
                                    {
                                        App.Log("AR400 tagging " + tagProfile.Category + " in " + item.ViewName + " failed: " + ex);
                                        tagWarnings.Add(item.ViewName + ": tagging " + CategoryLabel(bic) + " failed — " + ex.Message);
                                    }
                                }

                            if (item.Profile.DimensionGrids || item.Profile.DimensionWalls)
                            {
                                try
                                {
                                    DimensionResult dr = DimensionView(doc, plan, item.Profile);
                                    dimensionsPlaced += dr.Created;
                                    if (dr.Note != null) tagWarnings.Add(item.ViewName + ": " + dr.Note);
                                }
                                catch (Exception ex)
                                {
                                    App.Log("AR400 dimensioning " + item.ViewName + " failed: " + ex);
                                    tagWarnings.Add(item.ViewName + ": dimensioning failed — " + ex.Message);
                                }
                            }

                            if (item.Profile.Schedule != null && item.Profile.Schedule.Enabled)
                            {
                                try
                                {
                                    if (PlaceScheduleOnSheet(doc, sheet, item.Level, item.Package, item.Profile.Schedule))
                                        schedulesPlaced++;
                                }
                                catch (Exception ex) { failed.Add(item.ViewName + " (schedule) — " + ex.Message); }
                            }

                            // Lay the sheet out only once everything that goes on it exists, so the view
                            // can be centred in whatever space the schedule leaves.
                            try { ArrangeSheet(doc, sheet, settings); }
                            catch (Exception ex) { App.Log("AR400 layout of " + item.ViewName + " failed: " + ex); }
                        }
                        txAnnotate.Commit();
                    }
                }

                // Assimilate merges both transactions into a SINGLE undo entry ("AR400 shop drawing set").
                if (created.Count > 0) group.Assimilate();
                else group.RollBack();   // nothing new was made — keep the undo stack clean
            }

            if (packagesWithoutTagConfig.Count > 0)
                tagWarnings.Add("No tag categories are configured for: " + string.Join(", ", packagesWithoutTagConfig.OrderBy(p => p)) +
                                " — set them in AR400 Settings → the \"Tags\" button on that package's row.");

            ShowSummary(created, skipped, failed, excelUnmatched, tagWarnings, templated, tagsPlaced, tagsMoved,
                schedulesPlaced, dimensionsPlaced, register != null ? register.Count : 0);
            return Result.Succeeded;
        }

        private sealed class DimensionResult { public int Created; public string Note; }

        // Automatic dimensioning inside a finished view. Two independent passes:
        //   • grid strings — one multi-segment dimension per grid direction, run outside the plan,
        //     which is the backbone of any architectural plan and is fully deterministic;
        //   • wall pairs  — real face-to-face clear dimensions between facing parallel walls, using
        //     the same ReferenceIntersector core as the create_wall_dimension tool.
        // The AI tie-breaker is deliberately NOT used here: it costs ~10s per call against a local
        // model, and a sheet with 20 dimensions would take minutes for a judgement the nearest-face
        // rule gets right almost every time.
        private static DimensionResult DimensionView(Document doc, View view, Ar400PackageProfile profile)
        {
            var result = new DimensionResult();
            var problems = new List<string>();

            // Re-runs must not stack duplicate dimensions on the same view.
            bool alreadyDimensioned = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Dimension)).Any();
            if (alreadyDimensioned) return result;

            if (profile.DimensionGrids)
            {
                try { result.Created += DimensionGridStrings(doc, view); }
                catch (Exception ex) { problems.Add("grid dimensions failed — " + ex.Message); }
            }

            if (profile.DimensionWalls)
            {
                try { result.Created += DimensionWallPairs(doc, view); }
                catch (Exception ex) { problems.Add("wall dimensions failed — " + ex.Message); }
            }

            if (problems.Count > 0) result.Note = string.Join("; ", problems);
            return result;
        }

        // One dimension string per grid direction (the horizontal run across the vertical grids, and
        // vice versa), placed just outside the grids' own extent.
        private static int DimensionGridStrings(Document doc, View view)
        {
            var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line).ToList();
            if (grids.Count < 2) return 0;

            int made = 0;
            // Group by orientation: grids running mostly north-south are spaced along X, and vice versa.
            foreach (bool spacedAlongX in new[] { true, false })
            {
                var group = grids.Where(g =>
                {
                    XYZ d = ((Line)g.Curve).Direction;
                    bool runsNorthSouth = Math.Abs(d.Y) > Math.Abs(d.X);
                    return runsNorthSouth == spacedAlongX;
                }).ToList();
                if (group.Count < 2) continue;

                // Position of each grid along the axis it is spaced on, and the extent of the whole set.
                var ordered = group
                    .Select(g => new { Grid = g, Line = (Line)g.Curve })
                    .OrderBy(x => spacedAlongX ? x.Line.GetEndPoint(0).X : x.Line.GetEndPoint(0).Y)
                    .ToList();

                double crossMax = ordered.Max(x => Math.Max(
                    spacedAlongX ? x.Line.GetEndPoint(0).Y : x.Line.GetEndPoint(0).X,
                    spacedAlongX ? x.Line.GetEndPoint(1).Y : x.Line.GetEndPoint(1).X));
                double offset = MmToFtLocal(2000);
                double crossAt = crossMax + offset;

                double first = spacedAlongX ? ordered.First().Line.GetEndPoint(0).X : ordered.First().Line.GetEndPoint(0).Y;
                double last = spacedAlongX ? ordered.Last().Line.GetEndPoint(0).X : ordered.Last().Line.GetEndPoint(0).Y;
                if (Math.Abs(last - first) < MmToFtLocal(500)) continue;

                double z = ordered.First().Line.GetEndPoint(0).Z;
                XYZ p0 = spacedAlongX ? new XYZ(first, crossAt, z) : new XYZ(crossAt, first, z);
                XYZ p1 = spacedAlongX ? new XYZ(last, crossAt, z) : new XYZ(crossAt, last, z);

                var refs = new ReferenceArray();
                foreach (var g in ordered) refs.Append(new Reference(g.Grid));

                try
                {
                    doc.Create.NewDimension(view, Line.CreateBound(p0, p1), refs);
                    made++;
                }
                catch { /* a grid set Revit won't dimension in this view — skip that direction */ }
            }
            return made;
        }

        // Finds facing parallel wall pairs and gives each a real face-to-face dimension. Pairing rules
        // are the ones validated against the live model: straight walls only, 500–6000 mm apart, at
        // least 1000 mm of overlap, and each wall dimensioned to its NEAREST partner only — otherwise a
        // room with four walls produces a thicket of near-duplicate dimensions.
        private static int DimensionWallPairs(Document doc, View view)
        {
            var walls = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType().Cast<Wall>()
                .Where(w => (w.Location as LocationCurve) != null && ((LocationCurve)w.Location).Curve is Line)
                .Take(200)   // a whole-floor plan can hold thousands; keep this bounded and quick
                .ToList();
            if (walls.Count < 2) return 0;

            const double MinGapMm = 500, MaxGapMm = 6000, MinOverlapMm = 1000;
            var donePairs = new HashSet<string>();
            int made = 0;

            // ONE scratch 3D view for the whole plan instead of one per dimension — creating and
            // deleting a view is expensive, and this loop can produce dozens of dimensions.
            ViewFamilyType vft3d = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft3d == null) return 0;
            View3D scratch = View3D.CreateIsometric(doc, vft3d.Id);

            try
            {
            for (int i = 0; i < walls.Count && made < 40; i++)
            {
                Wall a = walls[i];
                Line la = (Line)((LocationCurve)a.Location).Curve;
                XYZ da = la.Direction;
                XYZ perp = new XYZ(-da.Y, da.X, 0);

                Wall bestPartner = null;
                Line bestLine = null;
                double bestGap = double.MaxValue;
                XYZ bestProbe = null;

                for (int j = 0; j < walls.Count; j++)
                {
                    if (j == i) continue;
                    Wall b = walls[j];
                    Line lb = (Line)((LocationCurve)b.Location).Curve;
                    if (Math.Abs(da.DotProduct(lb.Direction)) < 0.999) continue;

                    double gap = Math.Abs((lb.GetEndPoint(0) - la.GetEndPoint(0)).DotProduct(perp));
                    if (gap < MmToFtLocal(MinGapMm) || gap > MmToFtLocal(MaxGapMm)) continue;

                    double a0 = la.GetEndPoint(0).DotProduct(da), a1 = la.GetEndPoint(1).DotProduct(da);
                    double b0 = lb.GetEndPoint(0).DotProduct(da), b1 = lb.GetEndPoint(1).DotProduct(da);
                    double lo = Math.Max(Math.Min(a0, a1), Math.Min(b0, b1));
                    double hi = Math.Min(Math.Max(a0, a1), Math.Max(b0, b1));
                    if (hi - lo < MmToFtLocal(MinOverlapMm)) continue;

                    if (gap < bestGap)
                    {
                        bestGap = gap;
                        bestPartner = b;
                        bestLine = lb;
                        double t = (((lo + hi) / 2) - a0) / (a1 - a0);
                        bestProbe = la.Evaluate(t, true);
                    }
                }

                if (bestPartner == null) continue;
                string key = Math.Min(a.Id.IntegerValue, bestPartner.Id.IntegerValue) + "-" +
                             Math.Max(a.Id.IntegerValue, bestPartner.Id.IntegerValue);
                if (!donePairs.Add(key)) continue;

                try
                {
                    string note;
                    Dimension d = ToolDispatcher.DimensionWallPair(doc, view, a, bestPartner, la, bestLine,
                        bestProbe, 0.0, false, out note, scratch);
                    if (d != null) made++;
                }
                catch { /* one awkward pair must not stop the rest */ }
            }
            }
            finally { try { doc.Delete(scratch.Id); } catch { } }
            return made;
        }

        // Arranges everything that sits on a finished sheet, inside the title block:
        //   • no schedule  → the view is centred on the sheet;
        //   • with schedule(s) → a right-hand column as wide as the widest schedule is reserved, the
        //     schedules stack down it from the top, and the view is centred in the space left of it.
        // Positions are computed from the REAL placed sizes (viewport outline, schedule bounding box),
        // never guessed, so it works for any sheet size and any title block.
        private static void ArrangeSheet(Document doc, ViewSheet sheet, Ar400Settings settings)
        {
            doc.Regenerate();   // sizes aren't known until Revit has drawn the new viewport/schedule

            BoundingBoxXYZ area = ToolDispatcher.SheetContentBounds(doc, sheet);
            if (area == null) return;

            // Keep clear of the title block's right-hand data strip (the خرطوشة column of project /
            // client / revision boxes) — content placed over it is unreadable. The user's saved
            // override wins; otherwise it is detected from the title block's own geometry.
            double strip = 0;
            Element titleBlock = ToolDispatcher.TitleBlockOf(doc, sheet);
            if (titleBlock != null && settings != null)
            {
                double overrideMm = settings.StripOverrideMm(titleBlock.GetTypeId().IntegerValue);
                strip = overrideMm > 0 ? MmToFtLocal(overrideMm)
                                       : ToolDispatcher.DetectTitleBlockStrip(doc, sheet, area);
            }

            double margin = MmToFtLocal(12);
            double gap = MmToFtLocal(10);
            double left = area.Min.X + margin, right = area.Max.X - strip - margin;
            double bottom = area.Min.Y + margin, top = area.Max.Y - margin;
            if (right <= left || top <= bottom) return;   // title block smaller than the margins

            var viewports = sheet.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).ToList();
            // Revision schedules live inside the title block family — they are not ours to move.
            var schedules = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).ToList();

            double viewRight = right;
            if (schedules.Count > 0)
            {
                double columnWidth = 0;
                foreach (ScheduleSheetInstance s in schedules)
                {
                    BoundingBoxXYZ b = s.get_BoundingBox(sheet);
                    if (b != null) columnWidth = Math.Max(columnWidth, b.Max.X - b.Min.X);
                }
                if (columnWidth <= 0) columnWidth = MmToFtLocal(80);   // unmeasurable — reserve a sane strip

                double columnLeft = right - columnWidth;
                double cursorY = top;
                foreach (ScheduleSheetInstance s in schedules)
                {
                    BoundingBoxXYZ b = s.get_BoundingBox(sheet);
                    if (b == null) continue;
                    // Shift by the delta between where its top-left IS and where we want it — no
                    // assumption about which corner ScheduleSheetInstance.Point refers to.
                    s.Point = s.Point + new XYZ(columnLeft - b.Min.X, cursorY - b.Max.Y, 0);
                    doc.Regenerate();
                    BoundingBoxXYZ moved = s.get_BoundingBox(sheet);
                    cursorY = (moved != null ? moved.Min.Y : cursorY - (b.Max.Y - b.Min.Y)) - gap;
                }

                double candidate = columnLeft - gap;
                if (candidate > left) viewRight = candidate;   // otherwise the schedule fills the sheet;
                                                              // keep the full width rather than squeeze to nothing
            }

            // AR400 puts exactly one view per sheet. If a sheet somehow has several, leave them alone
            // rather than stacking them all on the same point.
            if (viewports.Count != 1) return;
            try { viewports[0].SetBoxCenter(new XYZ((left + viewRight) / 2, (bottom + top) / 2, 0)); }
            catch { /* viewport pinned or otherwise immovable — leave it where Revit put it */ }
        }

        private static double MmToFtLocal(double mm) { return mm / 304.8; }

        // What phase 1 produced, carried over to phase 2 (annotation). Views/sheets are referenced by
        // ElementId rather than by object since they survive phase 1's commit and are re-fetched after.
        private sealed class BuiltSheet
        {
            public string Package;
            public Ar400PackageProfile Profile;
            public Level Level;
            public ElementId ViewId;
            public ElementId SheetId;
            public string ViewName;
        }

        private sealed class TagResult
        {
            public int Tagged;
            public int Moved;     // how many had to be nudged out of a clash
            public string Note;   // non-null only when something looks WRONG, not just "nothing to tag yet"
        }

        // Tags every element of 'category' visible in 'view', avoiding tag-on-tag clashes.
        //
        // Revit's own "Tag All Not Tagged" just drops every tag at its default anchor and leaves them
        // overlapping; this does the same placement but then measures each tag's REAL bounding box and,
        // if it overlaps one already placed in this view, tries a ring of candidate offsets around the
        // anchor (deterministic, cheap). The AI decision layer is consulted only when EVERY candidate
        // is still blocked — a genuinely dense cluster where "least bad" is a judgement call.
        //
        // If a category is hidden, AR400 NEVER edits the view template — that template is the user's own
        // standard and is shared by every view using it, so silently changing it would be a surprising,
        // far-reaching edit. When the template is what hides the category, AR400 reports it by name and
        // leaves it alone; the user decides. Only a view's OWN visibility (a view AR400 itself just
        // created, and only when the template isn't controlling V/G) is adjusted automatically.
        private static TagResult TagCategory(Document doc, View view, BuiltInCategory category,
            Ar400TagCategoryProfile tagProfile, bool leader, bool orientationVertical, List<BoundingBoxXYZ> occupied)
        {
            string visibilityNote = null;
            Category cat = Category.GetCategory(doc, category);
            if (cat != null)
            {
                bool templateControlsVisibility = false;
                string templateName = null;
                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    var tpl = doc.GetElement(view.ViewTemplateId) as View;
                    if (tpl != null)
                    {
                        templateName = tpl.Name;
                        templateControlsVisibility = !tpl.GetNonControlledTemplateParameterIds()
                            .Contains(new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL));
                    }
                }

                try
                {
                    if (view.GetCategoryHidden(cat.Id))
                    {
                        if (templateControlsVisibility)
                            visibilityNote = CategoryLabel(category) + " is hidden by view template '" + templateName +
                                             "'. AR400 did not change your template — turn that category on in the " +
                                             "template yourself if these tags are wanted.";
                        else
                        {
                            view.SetCategoryHidden(cat.Id, false);
                            doc.Regenerate();   // visibility change isn't reflected in collectors until regen
                        }
                    }
                }
                catch (Exception ex)
                {
                    visibilityNote = "Could not make " + CategoryLabel(category) + " visible in this view (" + ex.Message + ").";
                }
            }

            List<Element> elements = new FilteredElementCollector(doc, view.Id).OfCategory(category)
                .WhereElementIsNotElementType().ToList();

            if (elements.Count == 0)
            {
                int inProject = new FilteredElementCollector(doc).OfCategory(category)
                    .WhereElementIsNotElementType().GetElementCount();
                string emptyNote = inProject == 0
                    ? null   // genuinely nothing placed yet (e.g. rooms not drawn at blockwork stage)
                    : inProject + " " + CategoryLabel(category) + " exist in the project but none are " +
                      "visible in this view — it may be cropped by the scope box, or the view template " +
                      "may hide that category." + (visibilityNote != null ? " " + visibilityNote : "");
                return new TagResult { Tagged = 0, Note = emptyNote };
            }

            // Already-tagged elements shouldn't be double-tagged on a re-run, and every existing
            // annotation is an obstacle for the new ones. (Matches Revit's "Not Tagged" semantics.)
            // Room tags are RoomTag, everything else is IndependentTag — one sweep over all annotation
            // elements handles both. Deliberately NOT using OfClass(typeof(RoomTag)): Revit's class
            // filter rejects some tag classes outright, and one throw here would abort the whole run.
            var alreadyTagged = new HashSet<int>();
            foreach (Element existing in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                try
                {
                    var independent = existing as IndependentTag;
                    var roomTag = existing as RoomTag;
                    if (independent == null && roomTag == null) continue;

                    if (independent != null)
                        foreach (ElementId t in GetTaggedIds(independent)) alreadyTagged.Add(t.IntegerValue);
                    else if (roomTag.Room != null)
                        alreadyTagged.Add(roomTag.Room.Id.IntegerValue);

                    BoundingBoxXYZ bb = existing.get_BoundingBox(view);
                    if (bb != null) occupied.Add(bb);
                }
                catch { /* a single unreadable annotation must never abort the run */ }
            }

            TagOrientation orientation = orientationVertical ? TagOrientation.Vertical : TagOrientation.Horizontal;
            int tagged2 = 0, moved = 0, failures = 0;
            string firstError = null;
            var placed = new List<Element>();

            foreach (Element e in elements)
            {
                if (alreadyTagged.Contains(e.Id.IntegerValue)) continue;
                XYZ anchor = AnchorPointFor(e, view);
                if (anchor == null) continue;

                try
                {
                    // Rooms are the special case: Revit models their tags as RoomTag via NewRoomTag,
                    // and IndependentTag.Create refuses them — this is why "Room Tags" appeared to do
                    // nothing before. Everything else goes through the normal IndependentTag path.
                    var room = e as Room;
                    Element tagElement;
                    if (room != null)
                    {
                        RoomTag roomTag = doc.Create.NewRoomTag(
                            new LinkElementId(room.Id), new UV(anchor.X, anchor.Y), view.Id);
                        if (roomTag == null) { failures++; continue; }
                        // RoomTag uses its own SpatialElementTagOrientation enum, not TagOrientation.
                        roomTag.TagOrientation = orientationVertical
                            ? SpatialElementTagOrientation.Vertical
                            : SpatialElementTagOrientation.Horizontal;
                        if (tagProfile.TagTypeId.HasValue)
                        {
                            try { roomTag.ChangeTypeId(new ElementId(tagProfile.TagTypeId.Value)); } catch { }
                        }
                        tagElement = roomTag;
                    }
                    else
                    {
                        IndependentTag tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader,
                            TagMode.TM_ADDBY_CATEGORY, orientation, anchor);
                        if (tagProfile.TagTypeId.HasValue)
                        {
                            try { tag.ChangeTypeId(new ElementId(tagProfile.TagTypeId.Value)); } catch { }
                        }
                        tagElement = tag;
                    }

                    // Deliberately NOT regenerating here. Revit only computes a tag's box after a
                    // regeneration, but doing that per tag made this O(n) full-document regenerations —
                    // brutal on a plan with hundreds of rooms. Create them all first, regenerate ONCE
                    // below, then resolve overlaps.
                    placed.Add(tagElement);
                    tagged2++;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (firstError == null) firstError = ex.Message;
                }
            }

            // ONE regeneration for the whole category, then measure and de-clash. Tags are resolved in
            // creation order so each only has to avoid what is already settled.
            if (placed.Count > 0)
            {
                doc.Regenerate();
                foreach (Element tagElement in placed)
                {
                    try
                    {
                        if (ResolveTagClash(view, tagElement, occupied)) moved++;
                        BoundingBoxXYZ finalBox = tagElement.get_BoundingBox(view);
                        if (finalBox != null) occupied.Add(finalBox);
                    }
                    catch { /* an unmeasurable tag just doesn't take part in de-clashing */ }
                }
            }

            string note = null;
            if (tagged2 == 0 && failures > 0)
                note = "Could not tag " + CategoryLabel(category) + " — is a tag family loaded for it? (" + firstError + ")";
            else if (failures > 0)
                note = failures + " of " + elements.Count + " " + CategoryLabel(category) + " tag(s) failed (" + firstError + ")";
            else if (tagged2 == 0)
                note = "All " + elements.Count + " " + CategoryLabel(category) + " in this view were already tagged — nothing new to add.";
            // A template-level visibility change is worth telling the user about even on success.
            if (visibilityNote != null) note = note == null ? visibilityNote : note + " " + visibilityNote;

            return new TagResult { Tagged = tagged2, Moved = moved, Note = note };
        }

        // IndependentTag.TaggedLocalElementId is deprecated in newer Revit APIs in favour of
        // GetTaggedLocalElementIds(); use whichever this Revit version provides.
        private static IEnumerable<ElementId> GetTaggedIds(IndependentTag tag)
        {
            try { return tag.GetTaggedLocalElementIds(); }
            catch { return new List<ElementId>(); }
        }

        // Where a tag should sit before any clash resolution: an element's own location point, the
        // midpoint of its curve, or its bounding-box centre — same anchor logic AICon's tag_elements
        // tool already uses, so tags land where the user expects.
        private static XYZ AnchorPointFor(Element e, View view)
        {
            var lp = e.Location as LocationPoint;
            if (lp != null) return lp.Point;
            var lc = e.Location as LocationCurve;
            if (lc != null) return lc.Curve.Evaluate(0.5, true);
            BoundingBoxXYZ bb = e.get_BoundingBox(view);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : null;
        }

        // IndependentTag and RoomTag both have a TagHeadPosition but share no common base that exposes
        // it, so read/write it through these two small helpers instead of duplicating the whole
        // clash-resolution routine per tag type.
        private static XYZ GetTagHead(Element tag)
        {
            var independent = tag as IndependentTag;
            if (independent != null) return independent.TagHeadPosition;
            var roomTag = tag as RoomTag;
            return roomTag != null ? roomTag.TagHeadPosition : null;
        }

        private static bool SetTagHead(Element tag, XYZ position)
        {
            try
            {
                var independent = tag as IndependentTag;
                if (independent != null) { independent.TagHeadPosition = position; return true; }
                var roomTag = tag as RoomTag;
                if (roomTag != null) { roomTag.TagHeadPosition = position; return true; }
            }
            catch { }
            return false;
        }

        // Nudges 'tag' off any already-occupied box. Tries a deterministic ring of candidate offsets
        // (increasing radius, 8 directions) and takes the first clear one. Only if EVERY candidate is
        // still blocked does it ask the AI decision layer to pick the least-bad — and if that's
        // unavailable or unsure, the tag simply stays at its default spot (Revit's own behaviour).
        // Returns true if the tag was moved.
        private static bool ResolveTagClash(View view, Element tag, List<BoundingBoxXYZ> occupied)
        {
            BoundingBoxXYZ box = tag.get_BoundingBox(view);
            if (box == null || !OverlapsAny(box, occupied)) return false;

            XYZ head = GetTagHead(tag);
            if (head == null) return false;
            double w = Math.Max(box.Max.X - box.Min.X, 1e-6);
            double h = Math.Max(box.Max.Y - box.Min.Y, 1e-6);

            // 8 compass directions × 3 rings, in units of the tag's own size — a tag-sized step is the
            // smallest move that can actually clear an equally-sized neighbour.
            var dirs = new[]
            {
                new XYZ(1, 0, 0), new XYZ(0, 1, 0), new XYZ(-1, 0, 0), new XYZ(0, -1, 0),
                new XYZ(1, 1, 0), new XYZ(-1, 1, 0), new XYZ(1, -1, 0), new XYZ(-1, -1, 0)
            };
            var candidates = new List<XYZ>();
            for (int ring = 1; ring <= 3; ring++)
                foreach (XYZ d in dirs)
                    candidates.Add(new XYZ(head.X + d.X * w * ring * 0.9, head.Y + d.Y * h * ring * 1.2, head.Z));

            foreach (XYZ candidate in candidates)
            {
                if (OverlapsAny(Shift(box, candidate - head), occupied)) continue;
                return SetTagHead(tag, candidate);
            }

            // Every candidate is blocked — a genuinely dense cluster.
            //
            // This is where the AI tie-breaker used to run. It has been REMOVED from this path on
            // purpose: this method executes inside an OPEN Revit transaction on the UI thread, and the
            // call is a blocking HTTP request (~10 s against a local model, measured). One crowded plan
            // would freeze Revit for minutes while holding a transaction open — the exact thing a Revit
            // add-in must never do. The tag simply keeps its default position, which is what Revit's own
            // "Tag All" does anyway, so this is never worse than the native command.
            //
            // If per-tag AI placement is ever wanted, it must be done OUTSIDE the transaction: collect
            // the clashes first, close the transaction, ask, then reopen to apply.
            return false;
        }

        private static bool OverlapsAny(BoundingBoxXYZ box, List<BoundingBoxXYZ> others)
        {
            foreach (BoundingBoxXYZ o in others)
                if (box.Min.X < o.Max.X && box.Max.X > o.Min.X &&
                    box.Min.Y < o.Max.Y && box.Max.Y > o.Min.Y)
                    return true;
            return false;
        }

        private static BoundingBoxXYZ Shift(BoundingBoxXYZ box, XYZ delta)
        {
            return new BoundingBoxXYZ { Min = box.Min + delta, Max = box.Max + delta };
        }

        private static double FtToMmLocal(double ft) { return ft * 304.8; }

        private static string CategoryLabel(BuiltInCategory bic)
        {
            return bic.ToString().Replace("OST_", "");
        }

        // Creates (or reuses, on a re-run) a schedule for this package×level and places it on the
        // sheet. Schedules are NOT viewports — they need ScheduleSheetInstance, not Viewport.Create.
        private static bool PlaceScheduleOnSheet(Document doc, ViewSheet sheet, Level level, string pkg, Ar400ScheduleProfile prof)
        {
            string schedName = "AR400 - " + pkg + " - " + level.Name + " - " +
                (string.IsNullOrWhiteSpace(prof.Name) ? "Schedule" : prof.Name);

            ViewSchedule schedule = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>().FirstOrDefault(s => string.Equals(s.Name, schedName, StringComparison.OrdinalIgnoreCase));

            if (schedule == null)
            {
                var args = new Dictionary<string, object> { ["category"] = prof.Category, ["name"] = schedName };
                List<string> fields = DefaultScheduleFields(prof.Category);
                if (fields != null) args["fields"] = fields.Cast<object>().ToList();

                var result = (Dictionary<string, object>)ToolDispatcher.CreateSchedule(doc, args);
                schedule = doc.GetElement(new ElementId(Convert.ToInt32(result["id"]))) as ViewSchedule;

                // Best-effort: scope the table to this level only. If the API rejects the filter for
                // any reason, leave the schedule project-wide rather than failing the whole run.
                try
                {
                    SchedulableField levelField = schedule.Definition.GetSchedulableFields()
                        .FirstOrDefault(f => string.Equals(f.GetName(doc), "Level", StringComparison.OrdinalIgnoreCase));
                    if (levelField != null)
                    {
                        bool alreadyAdded = false;
                        ScheduleFieldId levelFieldId = null;
                        for (int i = 0; i < schedule.Definition.GetFieldCount(); i++)
                        {
                            ScheduleField f = schedule.Definition.GetField(i);
                            if (string.Equals(f.GetName(), "Level", StringComparison.OrdinalIgnoreCase))
                            { levelFieldId = f.FieldId; alreadyAdded = true; break; }
                        }
                        if (!alreadyAdded)
                        {
                            ScheduleField added = schedule.Definition.AddField(levelField);
                            added.IsHidden = true;
                            levelFieldId = added.FieldId;
                        }
                        schedule.Definition.AddFilter(new ScheduleFilter(levelFieldId, ScheduleFilterType.Equal, level.Id));
                    }
                }
                catch { /* filtering is a nice-to-have; an unfiltered (project-wide) schedule still works */ }
            }

            // View-scoped collector over the SHEET finds elements placed on it — same pattern used
            // elsewhere in this codebase (GetViewElements) for "what's in this view/sheet".
            bool alreadyPlaced = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>().Any(si => si.ScheduleId == schedule.Id);
            if (!alreadyPlaced)
                ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, new XYZ(0.5, 1.5, 0));
            return true;
        }

        // Sensible default columns per category so schedules aren't empty when the settings window
        // doesn't ask for specific fields. Returns null to fall back to CreateSchedule's own generic default.
        private static List<string> DefaultScheduleFields(string category)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "doors":
                case "windows":
                    return new List<string> { "Mark", "Family and Type", "Level", "Width", "Height", "Count" };
                case "walls":
                    return new List<string> { "Family and Type", "Level", "Length", "Area" };
                case "rooms":
                    return new List<string> { "Number", "Name", "Level", "Area" };
                case "ceilings":
                    return new List<string> { "Family and Type", "Level", "Area" };
                case "furniture":
                    return new List<string> { "Family and Type", "Level", "Count" };
                default:
                    return null;
            }
        }

        // One usable line of a real sheet register: its drawing number, its title, and a normalised
        // blob of every cell on that row (used for content matching — see FindRegisterRow).
        private sealed class RegisterRow
        {
            public string Number;
            public string Name;
            public string Haystack;
        }

        // Uppercase, letters+digits only. Makes "BLOCK WORK PLAN" and "Blockwork" comparable, which
        // matters because a register is written for humans, not for exact string equality.
        private static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private static readonly string[] NumberHeaders =
            { "submittal no", "submittal no.", "sheet number", "drawing number", "drawing no", "dwg no", "doc no", "document no", "number", "sheet no", "no" };
        private static readonly string[] TitleHeaders =
            { "drawing title", "sheet name", "description", "drawing name", "title", "name" };
        private static readonly string[] AnyHeaderHint =
            { "s/n", "sn", "no", "number", "title", "name", "description", "date", "scale", "rev", "revision", "status", "package", "level", "floor", "submittal" };

        /// <summary>
        /// Reads a real-world drawing register. Two things make these files awkward, and both are
        /// handled here rather than demanded of the user:
        ///   • the header row is usually NOT row 1 — there's a letterhead block (logos, client /
        ///     consultant / contractor) above it, so the header row is searched for;
        ///   • there are usually no separate Package and Level columns — both are embedded in one
        ///     "Drawing Title" cell like "BLOCK WORK PLAN - BS04 - 1:100".
        /// So instead of requiring a rigid layout, every row keeps a normalised blob of its own text
        /// and matching is done on content (see FindRegisterRow).
        /// </summary>
        private static List<RegisterRow> ReadRegister(string path, out string problem)
        {
            problem = null;
            List<string[]> grid;
            try { grid = AICon.Shared.FileTexts.TryReadFirstSheetGrid(path); }
            catch (Exception ex) { problem = "Could not read that workbook (" + ex.Message + ")."; return null; }
            if (grid == null || grid.Count == 0) { problem = "That workbook appears to be empty."; return null; }

            // Find the header row: the row in the first 40 that mentions the most known column words.
            int headerRow = -1, bestScore = 0;
            for (int r = 0; r < grid.Count && r < 40; r++)
            {
                int score = 0;
                foreach (string cell in grid[r])
                {
                    string c = (cell ?? "").Trim().ToLowerInvariant().TrimEnd(':', '.').Trim();
                    if (c.Length == 0) continue;
                    if (AnyHeaderHint.Any(h => c == h || c.StartsWith(h + " ") || c.EndsWith(" " + h))) score++;
                }
                if (score > bestScore) { bestScore = score; headerRow = r; }
            }
            if (headerRow < 0 || bestScore < 2)
            {
                problem = "Couldn't find a header row in that file (looked for columns like \"Drawing Title\" / \"Submittal No.\").";
                return null;
            }

            string[] headers = grid[headerRow];
            int numberCol = FindColumn(headers, NumberHeaders);
            int titleCol = FindColumn(headers, TitleHeaders);
            if (titleCol < 0 && numberCol < 0)
            {
                problem = "Found a header row (" + string.Join(" | ", headers.Where(h => !string.IsNullOrWhiteSpace(h))) +
                          ") but no drawing-title or drawing-number column in it.";
                return null;
            }

            var rows = new List<RegisterRow>();
            for (int r = headerRow + 1; r < grid.Count; r++)
            {
                string[] cells = grid[r];
                string name = titleCol >= 0 && titleCol < cells.Length ? (cells[titleCol] ?? "").Trim() : null;
                string number = numberCol >= 0 && numberCol < cells.Length ? (cells[numberCol] ?? "").Trim() : null;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(number)) continue;

                rows.Add(new RegisterRow
                {
                    Number = string.IsNullOrWhiteSpace(number) ? null : number,
                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                    Haystack = Squash(string.Join(" ", cells))
                });
            }

            if (rows.Count == 0) problem = "The header row was found but there are no data rows under it.";
            return rows;
        }

        private static int FindColumn(string[] headers, string[] candidates)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                string h = (headers[i] ?? "").Trim().ToLowerInvariant().TrimEnd(':', '.').Trim();
                if (h.Length == 0) continue;
                if (candidates.Any(c => h == c)) return i;
            }
            // Second pass: allow "contains", so "SUBMITTAL No." still matches when punctuation differs.
            for (int i = 0; i < headers.Length; i++)
            {
                string h = (headers[i] ?? "").Trim().ToLowerInvariant();
                if (h.Length == 0) continue;
                if (candidates.Any(c => h.Contains(c))) return i;
            }
            return -1;
        }

        /// <summary>
        /// Finds the register line for one package × level, by looking for a row whose text mentions
        /// BOTH — e.g. package "BLOCKWORK" and level "BS04" both appear in "BLOCK WORK PLAN - BS04 -
        /// 1:100" once spaces and punctuation are ignored. Returns null when nothing matches, so the
        /// caller can fall back to Revit's own numbering rather than guess wrong.
        /// </summary>
        private static RegisterRow FindRegisterRow(List<RegisterRow> register, string package, string levelName,
            Ar400Settings settings)
        {
            if (register == null) return null;
            // Projects often name levels differently in the model and in the register ("-01-BF04-PVL"
            // vs "BS04") — AR400 Settings → Levels holds the user's mapping when they differ.
            string registerLabel = settings != null ? settings.RegisterLabelForLevel(levelName) : levelName;
            string pkg = Squash(package);
            string lvl = Squash(registerLabel);
            if (pkg.Length == 0 || lvl.Length == 0) return null;

            RegisterRow match = register.FirstOrDefault(r => r.Haystack.Contains(pkg) && r.Haystack.Contains(lvl));
            if (match != null) return match;

            // Level names often differ between the model and the register ("-01-BF04-PVL" vs "BS04").
            // Fall back to the level's most distinctive token — its letters+digits run, e.g. "BF04" —
            // but ONLY together with the package, and only when exactly one row matches, so an
            // ambiguous guess is never applied silently.
            foreach (string token in DistinctiveTokens(registerLabel))
            {
                var hits = register.Where(r => r.Haystack.Contains(pkg) && r.Haystack.Contains(token)).ToList();
                if (hits.Count == 1) return hits[0];
            }
            return null;
        }

        // Longest-first alphanumeric chunks of a level name, e.g. "-01-BF04-PVL" → BF04, PVL, 01.
        private static IEnumerable<string> DistinctiveTokens(string levelName)
        {
            return (levelName ?? "")
                .Split(new[] { ' ', '-', '_', '.', '(', ')', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Squash)
                .Where(t => t.Length >= 3)
                .OrderByDescending(t => t.Length)
                .Distinct();
        }

        // A template matches when its name contains the package name (e.g. template
        // "SD - BLOCKWORK PLAN" for package "BLOCKWORK") and its view type fits the
        // plan being created. Shortest name wins so "BLOCKWORK" beats "BLOCKWORK OLD".
        // Used only as a fallback when the package's AR400 Settings don't specify a template.
        private static View FindTemplate(Document doc, string pkg, bool ceiling)
        {
            ViewType want = ceiling ? ViewType.CeilingPlan : ViewType.FloorPlan;
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == want &&
                            v.Name.IndexOf(pkg, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(v => v.Name.Length)
                .FirstOrDefault();
        }

        private static void ShowSummary(List<string> created, List<string> skipped, List<string> failed,
            List<string> excelUnmatched, List<string> tagWarnings, int templated, int tagsPlaced, int tagsMoved,
            int schedulesPlaced, int dimensionsPlaced, int registerRows)
        {
            var body = new StringBuilder();
            if (created.Count > 0)
            {
                body.AppendLine("Created:");
                foreach (string n in created.Take(25)) body.AppendLine("  •  " + n);
                if (created.Count > 25) body.AppendLine("  …  and " + (created.Count - 25) + " more");
                body.AppendLine();
                body.AppendLine(templated + " view(s) received a view template, " +
                                 tagsPlaced + " tag(s) placed" +
                                 (tagsMoved > 0 ? " (" + tagsMoved + " nudged apart to avoid overlaps)" : "") +
                                 ", " + schedulesPlaced + " schedule(s) placed on sheets" +
                                 (dimensionsPlaced > 0 ? ", " + dimensionsPlaced + " dimension(s) added" : "") + ".");
            }
            if (registerRows > 0)
                body.AppendLine().AppendLine("Sheet register: read " + registerRows + " row(s), matched " +
                                             (created.Count - excelUnmatched.Count) + " of " + created.Count + " sheet(s).");
            if (excelUnmatched.Count > 0)
            {
                body.AppendLine().AppendLine("No register row matched these (Revit auto-numbered them):");
                foreach (string n in excelUnmatched.Take(8)) body.AppendLine("  •  " + n);
                if (excelUnmatched.Count > 8) body.AppendLine("  …  and " + (excelUnmatched.Count - 8) + " more");
                body.AppendLine("A row matches when its text contains BOTH the package name and the level name. " +
                                "If your register names levels differently from the model (e.g. \"BS04\" vs \"-01-BF04-PVL\"), " +
                                "rename one side to match.");
            }
            if (tagWarnings.Count > 0)
            {
                body.AppendLine().AppendLine("Tag warnings:");
                foreach (string w in tagWarnings.Take(10)) body.AppendLine("  •  " + w);
            }
            if (skipped.Count > 0)
                body.AppendLine().AppendLine(skipped.Count + " view(s) already existed and were left untouched (sheet/tags/schedule not reapplied).");
            if (failed.Count > 0)
            {
                body.AppendLine().AppendLine("Failed:");
                foreach (string n in failed.Take(10)) body.AppendLine("  •  " + n);
            }

            new TaskDialog("AR400")
            {
                MainInstruction = created.Count > 0
                    ? created.Count + " shop drawing sheet(s) created"
                    : "No new sheets created",
                MainContent = body.ToString().TrimEnd(),
                FooterText = created.Count > 0 ? "One Ctrl+Z removes all of them." : null
            }.Show();
        }
    }
}
