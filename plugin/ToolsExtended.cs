using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Microsoft.CSharp;

namespace AICon
{
    using AICon.Routines;

    /// <summary>AICon v2 tools: views, sheets, graphics, analysis, exports, and the run_code escape hatch.</summary>
    internal static partial class ToolDispatcher
    {
        private static string ExportFolder()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AICon Exports");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static View ResolveView(Document doc, Dictionary<string, object> args, string key = "view_id")
        {
            int? viewId = Json.GetInt(args, key);
            if (!viewId.HasValue)
            {
                if (doc.ActiveView == null) throw new InvalidOperationException("No active view.");
                return doc.ActiveView;
            }
            var view = doc.GetElement(new ElementId(viewId.Value)) as View;
            if (view == null) throw new InvalidOperationException("view_id " + viewId.Value + " is not a view. Use list_views.");
            return view;
        }

        // ---------- read / query ----------

        private static object ListSheets(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.SheetNumber)
                .Select(s => (object)new Dictionary<string, object>
                {
                    { "id", s.Id.IntegerValue },
                    { "number", s.SheetNumber },
                    { "name", s.Name },
                    { "views_on_sheet", s.GetAllPlacedViews().Count }
                }).ToList();
        }

        private static object GetScheduleData(Document doc, Dictionary<string, object> args)
        {
            int? viewId = Json.GetInt(args, "view_id");
            if (!viewId.HasValue)
            {
                return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => (object)new Dictionary<string, object> { { "id", v.Id.IntegerValue }, { "name", v.Name } })
                    .ToList();
            }

            var schedule = doc.GetElement(new ElementId(viewId.Value)) as ViewSchedule;
            if (schedule == null) throw new InvalidOperationException("view_id " + viewId.Value + " is not a schedule. Call get_schedule_data without arguments to list schedules.");

            int maxRows = Json.GetInt(args, "limit") ?? 200;
            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            var rows = new List<object>();
            for (int r = 0; r < Math.Min(body.NumberOfRows, maxRows); r++)
            {
                var cells = new List<object>();
                for (int c = 0; c < body.NumberOfColumns; c++)
                    cells.Add(schedule.GetCellText(SectionType.Body, r, c));
                rows.Add(cells);
            }
            return new Dictionary<string, object>
            {
                { "schedule", schedule.Name },
                { "total_rows", body.NumberOfRows },
                { "rows", rows }
            };
        }

        /// <summary>
        /// Reads the parameter name under any of the spellings models actually emit. Necessary because
        /// AICon's own tools are inconsistent — create_view_filter takes "parameter", most others take
        /// "parameter_name" — and a model that learns one spelling reasonably reuses it everywhere.
        /// Accepting both is cheaper and kinder than expecting a 7B model to remember which is which.
        /// </summary>
        private static string ParamNameArg(Dictionary<string, object> args)
        {
            return Json.GetString(args, "parameter_name")
                ?? Json.GetString(args, "parameter")
                ?? Json.GetString(args, "param")
                ?? Json.GetString(args, "parameterName");
        }

        /// <summary>Names only what is ACTUALLY missing — saying "x and y are required" when y was
        /// supplied sends the model hunting for the wrong mistake.</summary>
        private static string MissingParamNameOrValue(string paramName, string value)
        {
            if (paramName == null && value == null)
                return "'parameter_name' and 'value' are both required.";
            if (paramName == null)
                return "'parameter_name' is required (you sent a value but no parameter name). " +
                       "Use \"parameter_name\", e.g. {\"parameter_name\":\"Unconnected Height\"}.";
            return "'value' is required (you sent parameter_name '" + paramName + "' but no value to compare against).";
        }

        private static object FilterElements(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            string paramName = ParamNameArg(args);
            string op = (Json.GetString(args, "operator") ?? "equals").ToLowerInvariant();
            string value = Json.GetString(args, "value");
            if (paramName == null || value == null)
                throw new InvalidOperationException(MissingParamNameOrValue(paramName, value));
            int limit = Json.GetInt(args, "limit") ?? 300;

            double numericQuery;
            bool queryIsNumeric = double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out numericQuery);

            var matches = new List<object>();
            int scanned = 0;
            foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                scanned++;
                Parameter p = e.LookupParameter(paramName);
                if (p == null && e.GetTypeId() != ElementId.InvalidElementId)
                    p = doc.GetElement(e.GetTypeId())?.LookupParameter(paramName);
                if (p == null) continue;

                bool match = false;
                if (queryIsNumeric && p.StorageType == StorageType.Double)
                {
                    double actual = p.AsDouble();
                    ForgeTypeId spec = p.Definition.GetDataType();
                    if (spec == SpecTypeId.Length) actual = FtToMm(actual);
                    else if (spec == SpecTypeId.Angle) actual = actual * 180.0 / Math.PI;
                    switch (op)
                    {
                        case "greater": match = actual > numericQuery; break;
                        case "less": match = actual < numericQuery; break;
                        case "not_equals": match = Math.Abs(actual - numericQuery) > 0.01; break;
                        default: match = Math.Abs(actual - numericQuery) <= 0.01; break;
                    }
                }
                else if (queryIsNumeric && p.StorageType == StorageType.Integer)
                {
                    int actual = p.AsInteger();
                    switch (op)
                    {
                        case "greater": match = actual > numericQuery; break;
                        case "less": match = actual < numericQuery; break;
                        case "not_equals": match = actual != (int)numericQuery; break;
                        default: match = actual == (int)numericQuery; break;
                    }
                }
                else
                {
                    string actual = ParameterValueString(p) ?? "";
                    switch (op)
                    {
                        case "contains": match = actual.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0; break;
                        case "not_equals": match = !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase); break;
                        default: match = string.Equals(actual, value, StringComparison.OrdinalIgnoreCase); break;
                    }
                }

                if (match)
                {
                    matches.Add(new Dictionary<string, object>
                    {
                        { "id", e.Id.IntegerValue },
                        { "name", e.Name },
                        { "value", ParameterValueString(p) }
                    });
                    if (matches.Count >= limit) break;
                }
            }
            return new Dictionary<string, object>
            {
                { "scanned", scanned },
                { "matched", matches.Count },
                { "elements", matches }
            };
        }

        private static object QuantitiesByType(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            var byType = new Dictionary<string, double[]>(); // count, area ft2, volume ft3, length ft

            foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                Element type = e.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(e.GetTypeId()) : null;
                string key = type != null ? type.Name : "(no type)";
                if (!byType.TryGetValue(key, out double[] acc)) { acc = new double[4]; byType[key] = acc; }
                acc[0] += 1;

                Parameter area = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED) ?? e.LookupParameter("Area");
                Parameter volume = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED) ?? e.LookupParameter("Volume");
                Parameter length = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH) ?? e.LookupParameter("Length");
                if (area != null && area.StorageType == StorageType.Double) acc[1] += area.AsDouble();
                if (volume != null && volume.StorageType == StorageType.Double) acc[2] += volume.AsDouble();
                if (length != null && length.StorageType == StorageType.Double) acc[3] += length.AsDouble();
            }

            return byType.OrderByDescending(kv => kv.Value[0]).Select(kv => (object)new Dictionary<string, object>
            {
                { "type", kv.Key },
                { "count", (int)kv.Value[0] },
                { "area_m2", Math.Round(kv.Value[1] * SqFtToSqM, 2) },
                { "volume_m3", Math.Round(kv.Value[2] * 0.028316846592, 2) },
                { "length_m", Math.Round(kv.Value[3] * 0.3048, 2) }
            }).ToList();
        }

        private static object DetectClashes(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory catA = ParseCategory(Json.GetString(args, "category_a"));
            BuiltInCategory catB = ParseCategory(Json.GetString(args, "category_b"));
            int limit = Json.GetInt(args, "limit") ?? 50;

            var clashes = new List<object>();
            var seen = new HashSet<string>();
            var elementsA = new FilteredElementCollector(doc).OfCategory(catA).WhereElementIsNotElementType().ToElements();

            foreach (Element a in elementsA.Take(500))
            {
                BoundingBoxXYZ bb = a.get_BoundingBox(null);
                if (bb == null) continue;
                var outline = new Outline(bb.Min, bb.Max);
                var hits = new FilteredElementCollector(doc)
                    .OfCategory(catB).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline));
                foreach (Element b in hits)
                {
                    if (b.Id == a.Id) continue;
                    string pairKey = Math.Min(a.Id.IntegerValue, b.Id.IntegerValue) + "_" + Math.Max(a.Id.IntegerValue, b.Id.IntegerValue);
                    if (!seen.Add(pairKey)) continue;
                    clashes.Add(new Dictionary<string, object>
                    {
                        { "element_a", a.Id.IntegerValue }, { "name_a", a.Name },
                        { "element_b", b.Id.IntegerValue }, { "name_b", b.Name }
                    });
                    if (clashes.Count >= limit)
                        return new Dictionary<string, object>
                        {
                            { "note", "Bounding-box based clash check (approximate). Limit reached; there may be more." },
                            { "clashes", clashes }
                        };
                }
            }
            return new Dictionary<string, object>
            {
                { "note", "Bounding-box based clash check (approximate)." },
                { "clashes", clashes }
            };
        }

        // ---------- views & sheets ----------

        private static object SetActiveView(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            uidoc.RequestViewChange(view);
            return new Dictionary<string, object> { { "requested_view", view.Name } };
        }

        private static ViewFamilyType GetViewFamilyType(Document doc, ViewFamily family)
        {
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == family);
            if (vft == null) throw new InvalidOperationException("No view type for " + family + " found in this project.");
            return vft;
        }

        private static object CreateFloorPlan(Document doc, Dictionary<string, object> args)
        {
            Level level = ResolveLevel(doc, args);
            ViewPlan plan = ViewPlan.Create(doc, GetViewFamilyType(doc, ViewFamily.FloorPlan).Id, level.Id);
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { plan.Name = name; } catch { } }
            return new Dictionary<string, object> { { "id", plan.Id.IntegerValue }, { "name", plan.Name }, { "level", level.Name } };
        }

        private static object Create3DView(Document doc, Dictionary<string, object> args)
        {
            View3D view = View3D.CreateIsometric(doc, GetViewFamilyType(doc, ViewFamily.ThreeDimensional).Id);
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { view.Name = name; } catch { } }
            return new Dictionary<string, object> { { "id", view.Id.IntegerValue }, { "name", view.Name } };
        }

        private static object DuplicateView(Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            string mode = (Json.GetString(args, "mode") ?? "with_detailing").ToLowerInvariant();
            ViewDuplicateOption option =
                mode == "as_dependent" ? ViewDuplicateOption.AsDependent :
                mode == "plain" ? ViewDuplicateOption.Duplicate :
                ViewDuplicateOption.WithDetailing;
            ElementId newId = view.Duplicate(option);
            var newView = doc.GetElement(newId) as View;
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { newView.Name = name; } catch { } }
            return new Dictionary<string, object> { { "id", newId.IntegerValue }, { "name", newView.Name } };
        }

        private static object CreateViewTemplate(Document doc, Dictionary<string, object> args)
        {
            // Accept the aliases models actually send; REQUIRE a name BEFORE creating anything —
            // a silently default-named template is a template the user can never find.
            string name = Json.GetString(args, "name") ?? Json.GetString(args, "template_name");
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("'name' is required — what should the new view template be called?");

            View source = ResolveView(doc, args, "source_view_id");
            if (source.IsTemplate)
                throw new InvalidOperationException("source_view_id is already a view template. Pick a normal view (use list_views).");
            View template;
            try { template = source.CreateViewTemplate(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "This view type ('" + source.ViewType + "') cannot produce a view template. " +
                    "Use a plan, section, elevation or 3D view as the source.");
            }
            try { template.Name = name; }
            catch (Exception)
            {
                doc.Delete(template.Id);   // don't leave an unfindable default-named template behind
                throw new InvalidOperationException(
                    "The name '" + name + "' is already used by another view template. Pick a different name.");
            }
            return new Dictionary<string, object>
            {
                { "id", template.Id.IntegerValue },
                { "name", template.Name },
                { "source_view", source.Name }
            };
        }

        private static object CreateViewFilter(Document doc, Dictionary<string, object> args)
        {
            string filterName = Json.GetString(args, "name") ?? Json.GetString(args, "filter_name");
            if (string.IsNullOrEmpty(filterName))
                throw new InvalidOperationException("'name' (the filter name) is required.");

            // Target view: view_id, or view_name (which may be a view TEMPLATE name), else active view.
            View view = ResolveViewByNameOrId(doc, args);

            bool hide = !(args.ContainsKey("hide") && args["hide"] is bool hh && !hh);

            // If a filter with this name already exists, REUSE it instead of failing with Revit's
            // "name already in use" error — small local models sometimes repeat a call that already
            // succeeded, and the retry-after-error path was spawning NAME_1, NAME_2… duplicates.
            var existingFilter = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(f => string.Equals(f.Name, filterName, StringComparison.OrdinalIgnoreCase));
            if (existingFilter != null)
            {
                if (!view.GetFilters().Contains(existingFilter.Id)) view.AddFilter(existingFilter.Id);
                view.SetFilterVisibility(existingFilter.Id, !hide);
                return new Dictionary<string, object>
                {
                    { "filter_id", existingFilter.Id.IntegerValue },
                    { "filter_name", existingFilter.Name },
                    { "applied_to", view.Name },
                    { "already_existed", true },
                    { "action", hide ? "hidden" : "visible" },
                    { "note", "A filter with this name already existed — it was applied to the view as-is (its rule was NOT changed). The task is DONE; do not call this tool again." }
                };
            }

            // Categories (default Walls so the most common ask "hide the X walls" just works).
            var catList = Json.GetList(args, "categories");
            var catIds = new List<ElementId>();
            var catNames = new List<string>();
            if (catList != null && catList.Count > 0)
                foreach (object c in catList) { var bic = ParseCategory(c == null ? null : c.ToString()); catIds.Add(new ElementId((int)bic)); catNames.Add(c.ToString()); }
            else { catIds.Add(new ElementId((int)BuiltInCategory.OST_Walls)); catNames.Add("Walls"); }

            // The parameter must be one Revit allows in view filters for these categories; match the
            // request's human name against the filterable set (built-ins via LabelUtils, project params
            // via ParameterElement.Name).
            string paramName = ParamNameArg(args);
            if (string.IsNullOrEmpty(paramName))
                throw new InvalidOperationException("'parameter' is required, e.g. 'Type Name', 'Comments', 'Family Name'.");
            ElementId paramId = ElementId.InvalidElementId;
            var available = new List<string>();
            foreach (ElementId pid in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, catIds))
            {
                string label = null;
                if (pid.IntegerValue < 0)
                {
                    try { label = LabelUtils.GetLabelFor((BuiltInParameter)pid.IntegerValue); } catch { }
                }
                else
                {
                    var pe = doc.GetElement(pid) as ParameterElement;
                    if (pe != null) label = pe.Name;
                }
                if (label == null) continue;
                available.Add(label);
                if (string.Equals(label, paramName, StringComparison.OrdinalIgnoreCase)) paramId = pid;
            }
            if (paramId == ElementId.InvalidElementId)
                throw new InvalidOperationException(
                    "Parameter '" + paramName + "' is not filterable for " + string.Join(", ", catNames) +
                    ". Filterable examples: " + string.Join(", ", available.OrderBy(a => a).Take(25)) + ".");

            string op = (Json.GetString(args, "operator") ?? "equals").ToLowerInvariant();
            string value = Json.GetString(args, "value") ?? "";
            FilterRule rule;
            switch (op)
            {
                case "equals": rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, value); break;
                case "not_equals": rule = ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, value); break;
                case "contains": rule = ParameterFilterRuleFactory.CreateContainsRule(paramId, value); break;
                case "not_contains": rule = ParameterFilterRuleFactory.CreateNotContainsRule(paramId, value); break;
                case "begins_with": rule = ParameterFilterRuleFactory.CreateBeginsWithRule(paramId, value); break;
                case "ends_with": rule = ParameterFilterRuleFactory.CreateEndsWithRule(paramId, value); break;
                case "greater":
                case "less":
                    {
                        double num;
                        if (!double.TryParse(value, out num))
                            throw new InvalidOperationException("Operator '" + op + "' needs a numeric 'value'.");
                        rule = op == "greater"
                            ? ParameterFilterRuleFactory.CreateGreaterRule(paramId, num, 0.001)
                            : ParameterFilterRuleFactory.CreateLessRule(paramId, num, 0.001);
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        "Unknown operator '" + op + "'. Use equals, not_equals, contains, not_contains, begins_with, ends_with, greater, less.");
            }

            var pfe = ParameterFilterElement.Create(doc, filterName, catIds,
                new ElementParameterFilter(rule));

            // Attach to the view/template. Default behaviour: hide what the filter matches.
            view.AddFilter(pfe.Id);
            view.SetFilterVisibility(pfe.Id, !hide);

            return new Dictionary<string, object>
            {
                { "filter_id", pfe.Id.IntegerValue },
                { "filter_name", pfe.Name },
                { "applied_to", view.Name },
                { "is_template", view.IsTemplate },
                { "action", hide ? "hidden" : "visible" },
                { "rule", string.Join(", ", catNames) + " where '" + paramName + "' " + op + " '" + value + "'" }
            };
        }

        // Resolve a target view by view_name (case-insensitive, includes templates) or view_id,
        // falling back to the active view. Exact name first; if that misses and exactly ONE view
        // contains the given text, that one is used (models often give partial names).
        private static View ResolveViewByNameOrId(Document doc, Dictionary<string, object> args)
        {
            string vn = Json.GetString(args, "view_name");
            if (Json.GetInt(args, "view_id").HasValue || string.IsNullOrEmpty(vn))
                return ResolveView(doc, args);
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            View view = all.FirstOrDefault(v => string.Equals(v.Name, vn, StringComparison.OrdinalIgnoreCase));
            if (view == null)
            {
                var partial = all.Where(v => v.Name.IndexOf(vn, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (partial.Count == 1) view = partial[0];
                else if (partial.Count > 1)
                    throw new InvalidOperationException("'" + vn + "' matches several views: " +
                        string.Join(", ", partial.Take(10).Select(v => v.Name)) + ". Use the exact name.");
            }
            if (view == null)
                throw new InvalidOperationException("No view named '" + vn + "'. Use list_views.");
            return view;
        }

        private static object ApplyViewTemplate(Document doc, Dictionary<string, object> args)
        {
            bool remove = args.ContainsKey("remove") && args["remove"] is bool r && r;

            View template = null;
            if (!remove)
            {
                var allTemplates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate).ToList();
                int? tId = Json.GetInt(args, "template_id");
                string tName = Json.GetString(args, "template_name") ?? Json.GetString(args, "name");
                if (tId.HasValue) template = doc.GetElement(new ElementId(tId.Value)) as View;
                else if (!string.IsNullOrEmpty(tName))
                {
                    template = allTemplates.FirstOrDefault(
                        v => string.Equals(v.Name, tName, StringComparison.OrdinalIgnoreCase));
                    if (template == null)
                    {
                        // Partial-name rescue: if exactly one template contains the given text, use it.
                        var partial = allTemplates.Where(
                            v => v.Name.IndexOf(tName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (partial.Count == 1) template = partial[0];
                    }
                }
                if (template == null || !template.IsTemplate)
                    throw new InvalidOperationException(
                        "View template '" + (tName ?? (tId.HasValue ? tId.Value.ToString() : "?")) + "' not found. " +
                        "Templates that EXIST in this project: " +
                        (allTemplates.Count == 0 ? "(none)" :
                            string.Join(", ", allTemplates.OrderBy(v => v.Name).Take(25).Select(v => v.Name))) +
                        ". Use one of these exact names.");
            }

            // Targets: view_ids array, or a single view_name / view_id, else the active view.
            var targets = new List<View>();
            var idList = Json.GetList(args, "view_ids");
            if (idList != null && idList.Count > 0)
                foreach (object o in idList)
                {
                    var v = doc.GetElement(new ElementId(Json.ToInt(o))) as View;
                    if (v == null) throw new InvalidOperationException("view_ids contains " + o + " which is not a view.");
                    targets.Add(v);
                }
            else targets.Add(ResolveViewByNameOrId(doc, args));

            var applied = new List<object>();
            foreach (View v in targets)
            {
                if (v.IsTemplate)
                    throw new InvalidOperationException("'" + v.Name + "' is itself a template — pick the target views instead.");
                v.ViewTemplateId = remove ? ElementId.InvalidElementId : template.Id;
                applied.Add(v.Name);
            }
            return new Dictionary<string, object>
            {
                { "template", remove ? "(removed)" : template.Name },
                { "applied_to", applied },
                { "count", applied.Count }
            };
        }

        private static object GetViewElements(Document doc, Dictionary<string, object> args)
        {
            View view = ResolveViewByNameOrId(doc, args);
            var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            string cat = Json.GetString(args, "category");
            if (!string.IsNullOrEmpty(cat)) collector = collector.OfCategory(ParseCategory(cat));

            int limit = Json.GetInt(args, "limit") ?? 100;
            var byCat = new Dictionary<string, int>();
            var list = new List<object>();
            int total = 0;
            foreach (Element e in collector)
            {
                total++;
                string c = e.Category != null ? e.Category.Name : "(none)";
                byCat[c] = byCat.TryGetValue(c, out int n) ? n + 1 : 1;
                if (list.Count < limit)
                    list.Add(new Dictionary<string, object> { { "id", e.Id.IntegerValue }, { "category", c }, { "name", e.Name } });
            }
            return new Dictionary<string, object>
            {
                { "view", view.Name },
                { "total", total },
                { "counts_by_category", byCat.OrderByDescending(kv => kv.Value).Take(30)
                    .Select(kv => (object)new Dictionary<string, object> { { "category", kv.Key }, { "count", kv.Value } }).ToList() },
                { "elements", list }
            };
        }

        internal static object CreateSchedule(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            ViewSchedule sched = ViewSchedule.CreateSchedule(doc, new ElementId((int)bic));
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { sched.Name = name; } catch { } }

            IList<SchedulableField> schedulable = sched.Definition.GetSchedulableFields();
            var availableNames = schedulable.Select(sf => sf.GetName(doc)).ToList();
            var added = new List<string>();

            var wanted = Json.GetList(args, "fields");
            if (wanted != null && wanted.Count > 0)
            {
                foreach (object w in wanted)
                {
                    string fname = w == null ? null : w.ToString();
                    SchedulableField match = schedulable.FirstOrDefault(
                        sf => string.Equals(sf.GetName(doc), fname, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                        throw new InvalidOperationException(
                            "Field '" + fname + "' is not schedulable for this category. Available: " +
                            string.Join(", ", availableNames.OrderBy(a => a).Take(40)) + ".");
                    added.Add(sched.Definition.AddField(match).GetName());
                }
            }
            else
            {
                // No fields requested — add a sensible default set so the schedule isn't empty.
                string[] preferred = { "Family and Type", "Type", "Level", "Area", "Volume", "Length", "Count", "Mark" };
                foreach (string p in preferred)
                {
                    SchedulableField sf = schedulable.FirstOrDefault(
                        x => string.Equals(x.GetName(doc), p, StringComparison.OrdinalIgnoreCase));
                    if (sf != null) added.Add(sched.Definition.AddField(sf).GetName());
                    if (added.Count >= 5) break;
                }
                if (added.Count == 0 && schedulable.Count > 0)
                    added.Add(sched.Definition.AddField(schedulable[0]).GetName());
            }

            string sortBy = Json.GetString(args, "sort_by");
            if (!string.IsNullOrEmpty(sortBy))
                for (int i = 0; i < sched.Definition.GetFieldCount(); i++)
                {
                    ScheduleField f = sched.Definition.GetField(i);
                    if (string.Equals(f.GetName(), sortBy, StringComparison.OrdinalIgnoreCase))
                    {
                        sched.Definition.AddSortGroupField(new ScheduleSortGroupField(f.FieldId, ScheduleSortOrder.Ascending));
                        break;
                    }
                }

            return new Dictionary<string, object>
            {
                { "id", sched.Id.IntegerValue },
                { "name", sched.Name },
                { "fields", added }
            };
        }

        private static object CreateSection(Document doc, Dictionary<string, object> args)
        {
            XYZ p0 = PointFromArg(args, "start", 2);
            XYZ p1 = PointFromArg(args, "end", 2);
            double zMin = MmToFt(Json.GetDouble(args, "bottom_mm") ?? 0.0);
            double zMax = MmToFt(Json.GetDouble(args, "top_mm") ?? 4000.0);
            double depth = MmToFt(Json.GetDouble(args, "depth_mm") ?? 3000.0);
            if (zMax <= zMin) throw new InvalidOperationException("top_mm must be greater than bottom_mm.");

            XYZ dirVec = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
            double len = dirVec.GetLength();
            if (len < 0.01) throw new InvalidOperationException("'start' and 'end' are the same point.");
            XYZ dir = dirVec.Normalize();

            // Section box: X along the cut line, Y up, Z = viewing direction (right-hand rule).
            var t = Transform.Identity;
            t.Origin = new XYZ((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2, (zMin + zMax) / 2);
            t.BasisX = dir;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = dir.CrossProduct(XYZ.BasisZ);
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-len / 2, -(zMax - zMin) / 2, 0),
                Max = new XYZ(len / 2, (zMax - zMin) / 2, depth)
            };

            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Section);
            if (vft == null) throw new InvalidOperationException("This project has no Section view family type.");

            ViewSection section = ViewSection.CreateSection(doc, vft.Id, box);
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { section.Name = name; } catch { } }
            return new Dictionary<string, object>
            {
                { "id", section.Id.IntegerValue },
                { "name", section.Name },
                { "length_mm", Math.Round(FtToMm(len), 1) }
            };
        }

        private static object CreateDimension(Document doc, Dictionary<string, object> args)
        {
            View view = ResolveViewByNameOrId(doc, args);
            var ids = Json.GetList(args, "element_ids");
            if (ids == null || ids.Count < 2)
                throw new InvalidOperationException("'element_ids' needs at least 2 elements. Grids work best; columns also work.");

            var refs = new ReferenceArray();
            var pts = new List<XYZ>();
            foreach (object o in ids)
            {
                Element e = doc.GetElement(new ElementId(Json.ToInt(o)));
                if (e == null) throw new InvalidOperationException("Element " + o + " not found.");
                refs.Append(new Reference(e));

                XYZ pt;
                var grid = e as Grid;
                var gl = grid != null ? grid.Curve as Line : null;
                if (gl != null) pt = gl.Evaluate(0.5, true);
                else
                {
                    BoundingBoxXYZ bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                    if (bb == null) throw new InvalidOperationException("Element " + o + " has no geometry to dimension.");
                    pt = (bb.Min + bb.Max) * 0.5;
                }
                pts.Add(pt);
            }

            XYZ d = (pts[pts.Count - 1] - pts[0]);
            if (d.GetLength() < 0.01)
                throw new InvalidOperationException("The elements are at the same location — nothing to dimension.");
            d = d.Normalize();
            double off = MmToFt(Json.GetDouble(args, "offset_mm") ?? 1000.0);
            XYZ offsetDir = d.CrossProduct(view.ViewDirection).Normalize();
            Line dimLine = Line.CreateBound(pts[0] + offsetDir * off, pts[pts.Count - 1] + offsetDir * off);

            try
            {
                Dimension dim = doc.Create.NewDimension(view, dimLine, refs);
                return new Dictionary<string, object>
                {
                    { "id", dim.Id.IntegerValue },
                    { "segments", ids.Count - 1 },
                    { "view", view.Name }
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Revit rejected these references for dimensioning (" + ex.Message +
                    "). Dimensions work most reliably between GRIDS (use list_elements with category Grids); walls need face references which this tool does not extract.");
            }
        }

        // Internal (not in the public catalogue): one-round-trip project snapshot injected into the
        // chat panel's system prompt so the model knows real level/view/template names up front.
        private static object ProjectSnapshot(UIDocument uidoc, Document doc)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber).Select(s => s.SheetNumber + " " + s.Name).ToList();
            return new Dictionary<string, object>
            {
                { "project", doc.Title },
                { "active_view", doc.ActiveView != null ? doc.ActiveView.Name : "(none)" },
                { "levels", levels.Take(60).ToList() },
                { "level_count", levels.Count },
                { "view_templates", views.Where(v => v.IsTemplate).Select(v => v.Name).OrderBy(n => n).Take(40).ToList() },
                { "floor_plans", views.Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                    .Select(v => v.Name).OrderBy(n => n).Take(40).ToList() },
                { "section_count", views.Count(v => !v.IsTemplate && v.ViewType == ViewType.Section) },
                { "sheet_count", sheets.Count },
                { "sheets", sheets.Take(30).ToList() }
            };
        }

        private static object CreateRevision(Document doc, Dictionary<string, object> args)
        {
            Revision rev = Revision.Create(doc);
            string desc = Json.GetString(args, "description");
            if (!string.IsNullOrEmpty(desc)) rev.Description = desc;
            string date = Json.GetString(args, "date");
            if (!string.IsNullOrEmpty(date)) rev.RevisionDate = date;
            string by = Json.GetString(args, "issued_by");
            if (!string.IsNullOrEmpty(by)) rev.IssuedBy = by;

            var applied = new List<object>();
            var sheetIds = Json.GetList(args, "sheet_ids");
            if (sheetIds != null)
                foreach (object o in sheetIds)
                {
                    var sheet = doc.GetElement(new ElementId(Json.ToInt(o))) as ViewSheet;
                    if (sheet == null) throw new InvalidOperationException(o + " is not a sheet id. Use list_sheets.");
                    var ids = sheet.GetAdditionalRevisionIds();
                    if (!ids.Contains(rev.Id)) { ids.Add(rev.Id); sheet.SetAdditionalRevisionIds(ids); }
                    applied.Add(sheet.SheetNumber);
                }
            return new Dictionary<string, object>
            {
                { "id", rev.Id.IntegerValue },
                { "sequence", rev.SequenceNumber },
                { "description", rev.Description ?? "" },
                { "on_sheets", applied }
            };
        }

        private static object BatchRename(Document doc, Dictionary<string, object> args)
        {
            string kind = (Json.GetString(args, "kind") ?? "views").ToLowerInvariant();
            string find = Json.GetString(args, "find");
            string replace = Json.GetString(args, "replace") ?? "";
            string prefix = Json.GetString(args, "prefix");
            string suffix = Json.GetString(args, "suffix");
            string contains = Json.GetString(args, "only_containing");
            if (string.IsNullOrEmpty(find) && string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix))
                throw new InvalidOperationException("Give 'find' (+ 'replace'), or 'prefix', or 'suffix'.");

            IEnumerable<Element> pool;
            switch (kind)
            {
                case "views":
                    pool = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet).Cast<Element>();
                    break;
                case "sheets": pool = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)); break;
                case "levels": pool = new FilteredElementCollector(doc).OfClass(typeof(Level)); break;
                case "grids": pool = new FilteredElementCollector(doc).OfClass(typeof(Grid)); break;
                case "rooms":
                    pool = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType();
                    break;
                default:
                    throw new InvalidOperationException("kind must be views, sheets, levels, grids or rooms.");
            }

            var renamed = new List<object>();
            int failed = 0;
            foreach (Element e in pool.ToList())
            {
                string old = e.Name;
                if (!string.IsNullOrEmpty(contains) &&
                    old.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string nw = old;
                if (!string.IsNullOrEmpty(find))
                {
                    if (old.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    nw = System.Text.RegularExpressions.Regex.Replace(
                        old, System.Text.RegularExpressions.Regex.Escape(find), replace.Replace("$", "$$"),
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                if (!string.IsNullOrEmpty(prefix)) nw = prefix + nw;
                if (!string.IsNullOrEmpty(suffix)) nw = nw + suffix;
                if (nw == old) continue;
                try { e.Name = nw; renamed.Add(old + " -> " + nw); }
                catch { failed++; }   // duplicate names etc. — count and continue
            }
            return new Dictionary<string, object>
            {
                { "renamed_count", renamed.Count },
                { "failed", failed },
                { "renamed", renamed.Take(40).ToList() }
            };
        }

        private static object CreateElevation(Document doc, Dictionary<string, object> args)
        {
            XYZ loc = PointFromArg(args, "location", 2);
            View plan = ResolveViewByNameOrId(doc, args);
            if (!(plan is ViewPlan))
                throw new InvalidOperationException(
                    "The elevation marker must be placed in a floor plan — pass a plan's view_name/view_id or make a plan the active view.");

            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Elevation);
            if (vft == null) throw new InvalidOperationException("This project has no Elevation view family type.");

            ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, loc, plan.Scale);
            int index = Json.GetInt(args, "direction_index") ?? 0;   // 0..3 = the marker's four faces
            ViewSection elev = marker.CreateElevation(doc, plan.Id, index);
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) { try { elev.Name = name; } catch { } }
            return new Dictionary<string, object>
            {
                { "id", elev.Id.IntegerValue },
                { "name", elev.Name },
                { "marker_id", marker.Id.IntegerValue }
            };
        }

        private static object MeasureBetweenElements(Document doc, Dictionary<string, object> args)
        {
            var ids = Json.GetList(args, "element_ids");
            if (ids == null || ids.Count != 2)
                throw new InvalidOperationException("'element_ids' must contain exactly 2 element ids.");
            Element a = doc.GetElement(new ElementId(Json.ToInt(ids[0])));
            Element b = doc.GetElement(new ElementId(Json.ToInt(ids[1])));
            if (a == null || b == null) throw new InvalidOperationException("Element not found — check the ids.");
            BoundingBoxXYZ ba = a.get_BoundingBox(null);
            BoundingBoxXYZ bb = b.get_BoundingBox(null);
            if (ba == null || bb == null) throw new InvalidOperationException("One of the elements has no geometry.");

            XYZ ca = (ba.Min + ba.Max) * 0.5;
            XYZ cb = (bb.Min + bb.Max) * 0.5;

            // Axis-aligned clear gap between the two boxes (0 on axes where they overlap).
            double gx = Math.Max(0, Math.Max(bb.Min.X - ba.Max.X, ba.Min.X - bb.Max.X));
            double gy = Math.Max(0, Math.Max(bb.Min.Y - ba.Max.Y, ba.Min.Y - bb.Max.Y));
            double gz = Math.Max(0, Math.Max(bb.Min.Z - ba.Max.Z, ba.Min.Z - bb.Max.Z));

            return new Dictionary<string, object>
            {
                { "center_distance_mm", Math.Round(FtToMm(ca.DistanceTo(cb)), 1) },
                { "dx_mm", Math.Round(FtToMm(Math.Abs(cb.X - ca.X)), 1) },
                { "dy_mm", Math.Round(FtToMm(Math.Abs(cb.Y - ca.Y)), 1) },
                { "dz_mm", Math.Round(FtToMm(Math.Abs(cb.Z - ca.Z)), 1) },
                { "clear_gap_mm", Math.Round(FtToMm(Math.Sqrt(gx * gx + gy * gy + gz * gz)), 1) }
            };
        }

        internal static object CreateSheet(Document doc, Dictionary<string, object> args)
        {
            string number = Json.GetString(args, "number");
            string name = Json.GetString(args, "name");

            // Idempotency: small models loop-echo a successful result back as a fresh call (same
            // name + the number Revit just assigned). Revit rejects the duplicate number silently
            // and auto-assigns the next one, which used to spawn AR-002, AR-003… endlessly. If the
            // requested number (or, with no number, the exact name) already exists, return the
            // EXISTING sheet and create nothing.
            var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            ViewSheet existing = null;
            if (!string.IsNullOrEmpty(number))
                existing = allSheets.FirstOrDefault(sh => string.Equals(sh.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
            else if (!string.IsNullOrEmpty(name))
                existing = allSheets.FirstOrDefault(sh => string.Equals(sh.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return new Dictionary<string, object>
                {
                    { "id", existing.Id.IntegerValue },
                    { "number", existing.SheetNumber },
                    { "name", existing.Name },
                    { "already_existed", true },
                    { "note", "A sheet with this " + (string.IsNullOrEmpty(number) ? "name" : "number") +
                              " ALREADY EXISTS — nothing new was created. The task is DONE; do not call create_sheet again." }
                };

            ElementId titleBlockId = ElementId.InvalidElementId;
            int? tbId = Json.GetInt(args, "title_block_type_id");
            if (tbId.HasValue) titleBlockId = new ElementId(tbId.Value);
            else
            {
                var tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType().FirstElement();
                if (tb != null) titleBlockId = tb.Id;
            }

            ViewSheet sheet = ViewSheet.Create(doc, titleBlockId);
            if (!string.IsNullOrEmpty(number)) { try { sheet.SheetNumber = number; } catch { } }
            if (!string.IsNullOrEmpty(name)) { try { sheet.Name = name; } catch { } }
            return new Dictionary<string, object>
            {
                { "id", sheet.Id.IntegerValue },
                { "number", sheet.SheetNumber },
                { "name", sheet.Name }
            };
        }

        internal static object PlaceViewOnSheet(Document doc, Dictionary<string, object> args)
        {
            int? viewId = Json.GetInt(args, "view_id");
            int? sheetId = Json.GetInt(args, "sheet_id");
            if (!viewId.HasValue || !sheetId.HasValue)
                throw new InvalidOperationException("'view_id' and 'sheet_id' are required.");
            var view = doc.GetElement(new ElementId(viewId.Value)) as View;
            var sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
            if (view == null || sheet == null)
                throw new InvalidOperationException("Invalid view_id or sheet_id.");
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                throw new InvalidOperationException("This view cannot be placed on that sheet (already placed, or wrong view kind).");

            // Default to the CENTRE of the title block. The old fixed (1.0, 0.75) ft constant sat near
            // the sheet's origin corner, so views landed off the paper on anything but a tiny sheet.
            XYZ center = args.ContainsKey("location") ? PointFromArg(args, "location", 2) : SheetCentre(doc, sheet);
            Viewport vp = Viewport.Create(doc, sheet.Id, view.Id, center);
            return new Dictionary<string, object>
            {
                { "viewport_id", vp.Id.IntegerValue },
                { "sheet", sheet.SheetNumber + " - " + sheet.Name },
                { "view", view.Name }
            };
        }

        /// <summary>The printable area of a sheet, in sheet coordinates: the title block's own extent
        /// when one is placed, otherwise the sheet outline. Null only if neither can be determined.</summary>
        internal static BoundingBoxXYZ SheetContentBounds(Document doc, ViewSheet sheet)
        {
            Element titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
            if (titleBlock != null)
            {
                BoundingBoxXYZ bb = titleBlock.get_BoundingBox(sheet);
                if (bb != null) return bb;
            }
            BoundingBoxUV outline = sheet.Outline;
            if (outline == null) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(outline.Min.U, outline.Min.V, 0),
                Max = new XYZ(outline.Max.U, outline.Max.V, 0)
            };
        }

        internal static XYZ SheetCentre(Document doc, ViewSheet sheet)
        {
            BoundingBoxXYZ area = SheetContentBounds(doc, sheet);
            return area != null
                ? new XYZ((area.Min.X + area.Max.X) / 2, (area.Min.Y + area.Max.Y) / 2, 0)
                : new XYZ(1.0, 0.75, 0);   // last-resort fallback (no title block, no outline)
        }

        /// <summary>The title block instance placed on this sheet, or null.</summary>
        internal static Element TitleBlockOf(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
        }

        /// <summary>
        /// Width (in feet) of the title block's right-hand data strip — the tall column of project /
        /// client / revision boxes down the right edge that drawing content must stay clear of.
        ///
        /// Detected from the title block's own geometry: that strip is separated from the drawing area
        /// by a single near-full-height vertical line, so we collect every tall vertical line in the
        /// family, ignore the outer border, and take the LEFTMOST one sitting in the right-hand part of
        /// the sheet (leftmost = widest strip = the safest assumption). Returns 0 when nothing
        /// convincing is found, in which case the caller should use the whole sheet.
        /// </summary>
        internal static double DetectTitleBlockStrip(Document doc, ViewSheet sheet, BoundingBoxXYZ area)
        {
            Element titleBlock = TitleBlockOf(doc, sheet);
            if (titleBlock == null || area == null) return 0;

            GeometryElement geo;
            try { geo = titleBlock.get_Geometry(new Options { View = sheet, IncludeNonVisibleObjects = false }); }
            catch { return 0; }
            if (geo == null) return 0;

            double width = area.Max.X - area.Min.X;
            double height = area.Max.Y - area.Min.Y;
            if (width <= 0 || height <= 0) return 0;

            var xs = new List<double>();
            CollectTallVerticalLineXs(geo, height * 0.55, xs);
            if (xs.Count == 0) return 0;

            double borderTolerance = MmToFt(4);
            double rightRegionStart = area.Min.X + width * 0.55;   // the strip is always well right of centre
            double stripLeftEdge = double.MaxValue;
            foreach (double x in xs)
            {
                if (x > area.Max.X - borderTolerance) continue;   // that's the outer border, not the divider
                if (x < rightRegionStart) continue;               // too far left to be the strip divider
                if (x < stripLeftEdge) stripLeftEdge = x;
            }
            if (stripLeftEdge == double.MaxValue) return 0;

            double strip = area.Max.X - stripLeftEdge;
            // Sanity: a data strip is a slice, not most of the sheet.
            return (strip > MmToFt(15) && strip < width * 0.5) ? strip : 0;
        }

        private static void CollectTallVerticalLineXs(GeometryElement geo, double minLength, List<double> xs)
        {
            foreach (GeometryObject g in geo)
            {
                var instance = g as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement nested = null;
                    try { nested = instance.GetInstanceGeometry(); } catch { }
                    if (nested != null) CollectTallVerticalLineXs(nested, minLength, xs);
                    continue;
                }
                var line = g as Line;
                if (line == null) continue;
                XYZ dir = line.Direction;
                if (Math.Abs(dir.X) > 1e-6) continue;      // not vertical in sheet space
                if (line.Length < minLength) continue;
                xs.Add(line.GetEndPoint(0).X);
            }
        }

        // ---------- view graphics ----------

        private static object HideElements(UIDocument uidoc, Document doc, Dictionary<string, object> args, bool hide)
        {
            View view = ResolveView(doc, args);
            ICollection<ElementId> ids = RequireIds(doc, args);
            var eligible = ids.Where(id =>
            {
                Element e = doc.GetElement(id);
                return e != null && (!hide || e.CanBeHidden(view));
            }).ToList();
            if (eligible.Count == 0) throw new InvalidOperationException("None of these elements can be " + (hide ? "hidden" : "unhidden") + " in this view.");
            if (hide) view.HideElements(eligible); else view.UnhideElements(eligible);
            return new Dictionary<string, object> { { hide ? "hidden" : "unhidden", eligible.Count }, { "view", view.Name } };
        }

        private static object IsolateElements(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            ICollection<ElementId> ids = RequireIds(doc, args);
            view.IsolateElementsTemporary(ids);
            return new Dictionary<string, object> { { "isolated", ids.Count }, { "view", view.Name }, { "note", "Temporary isolate; use reset_isolate to restore." } };
        }

        private static object ResetIsolate(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            return new Dictionary<string, object> { { "view", view.Name }, { "status", "temporary hide/isolate cleared" } };
        }

        private static ElementId SolidFillPatternId(Document doc)
        {
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            return solid != null ? solid.Id : ElementId.InvalidElementId;
        }

        private static OverrideGraphicSettings BuildColorOverride(Document doc, int r, int g, int b, int transparency, bool halftone)
        {
            var color = new Color((byte)r, (byte)g, (byte)b);
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ElementId solid = SolidFillPatternId(doc);
            if (solid != ElementId.InvalidElementId)
            {
                ogs.SetSurfaceForegroundPatternId(solid);
                ogs.SetCutForegroundPatternId(solid);
            }
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetCutForegroundPatternColor(color);
            if (transparency > 0) ogs.SetSurfaceTransparency(Math.Min(100, transparency));
            if (halftone) ogs.SetHalftone(true);
            return ogs;
        }

        private static object OverrideElementColor(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            ICollection<ElementId> ids = RequireIds(doc, args);
            bool reset = Json.GetString(args, "reset") == "True" || (Json.GetInt(args, "reset") ?? 0) == 1
                         || (args.ContainsKey("reset") && args["reset"] is bool rb && rb);

            OverrideGraphicSettings ogs;
            if (reset) ogs = new OverrideGraphicSettings();
            else
            {
                List<object> rgb = Json.GetList(args, "color");
                if (rgb == null || rgb.Count < 3)
                    throw new InvalidOperationException("'color' must be [r,g,b] 0-255 (or pass reset=true to clear).");
                ogs = BuildColorOverride(doc, Json.ToInt(rgb[0]), Json.ToInt(rgb[1]), Json.ToInt(rgb[2]),
                    Json.GetInt(args, "transparency") ?? 0,
                    args.ContainsKey("halftone") && args["halftone"] is bool hb && hb);
            }

            foreach (ElementId id in ids) view.SetElementOverrides(id, ogs);
            return new Dictionary<string, object> { { reset ? "reset" : "colored", ids.Count }, { "view", view.Name } };
        }

        private static readonly int[][] Palette = new[]
        {
            new[] {230, 25, 75}, new[] {60, 180, 75}, new[] {255, 225, 25}, new[] {0, 130, 200},
            new[] {245, 130, 48}, new[] {145, 30, 180}, new[] {70, 240, 240}, new[] {240, 50, 230},
            new[] {210, 245, 60}, new[] {250, 190, 212}, new[] {0, 128, 128}, new[] {220, 190, 255},
            new[] {170, 110, 40}, new[] {128, 0, 0}, new[] {170, 255, 195}, new[] {128, 128, 0}
        };

        /// <summary>Colour names models actually type, plus [r,g,b] arrays. A model asked to colour
        /// something "yellow" says "yellow", not [255,255,0].</summary>
        private static int[] ParseColorArg(Dictionary<string, object> args, string key)
        {
            List<object> rgb = Json.GetList(args, key);
            if (rgb != null && rgb.Count >= 3)
                return new[] { Json.ToInt(rgb[0]), Json.ToInt(rgb[1]), Json.ToInt(rgb[2]) };

            string name = (Json.GetString(args, key) ?? "").Trim().ToLowerInvariant();
            switch (name)
            {
                case "yellow": return new[] { 255, 220, 0 };
                case "red": return new[] { 230, 25, 75 };
                case "green": return new[] { 60, 180, 75 };
                case "blue": return new[] { 0, 130, 200 };
                case "orange": return new[] { 245, 130, 48 };
                case "purple": case "violet": return new[] { 145, 30, 180 };
                case "cyan": case "turquoise": return new[] { 70, 240, 240 };
                case "magenta": case "pink": return new[] { 240, 50, 230 };
                case "brown": return new[] { 154, 99, 36 };
                case "grey": case "gray": return new[] { 128, 128, 128 };
                case "black": return new[] { 0, 0, 0 };
                case "white": return new[] { 255, 255, 255 };
                default: return null;
            }
        }

        /// <summary>
        /// Colour exactly the elements that match a condition, in one call — "colour every wall taller
        /// than 40000 in yellow". This exists because that request needs a FILTER, a CATEGORY and a
        /// CHOSEN COLOUR, and no single tool covered it: color_by_parameter groups everything by a
        /// parameter with automatic colours, and override_element_color needs ids you already have.
        /// Splitting it over two calls is reliable for a large model and hopeless for a small one.
        /// </summary>
        private static object ColorElementsWhere(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            string paramName = ParamNameArg(args);
            string value = Json.GetString(args, "value");
            if (paramName == null || value == null)
                throw new InvalidOperationException(MissingParamNameOrValue(paramName, value));

            int[] rgb = ParseColorArg(args, "color");
            if (rgb == null)
                throw new InvalidOperationException(
                    "'color' is required — either a name (yellow, red, green, blue, orange, purple, " +
                    "cyan, magenta, brown, grey, black, white) or [r,g,b] 0-255.");

            string op = (Json.GetString(args, "operator") ?? "greater").ToLowerInvariant();
            var matched = MatchElements(doc, view, bic, paramName, op, value, out string paramHint);
            if (matched.Count == 0)
                return new Dictionary<string, object>
                {
                    { "colored", 0 },
                    { "view", view.Name },
                    { "note", "No " + bic.ToString().Replace("OST_", "") + " in this view matched " +
                              paramName + " " + op + " " + value + "." + paramHint }
                };

            OverrideGraphicSettings ogs = BuildColorOverride(doc, rgb[0], rgb[1], rgb[2], 0, false);
            foreach (ElementId id in matched) view.SetElementOverrides(id, ogs);

            return new Dictionary<string, object>
            {
                { "colored", matched.Count },
                { "view", view.Name },
                { "rgb", new List<object> { rgb[0], rgb[1], rgb[2] } },
                { "element_ids", matched.Take(200).Select(i => (object)i.IntegerValue).ToList() }
            };
        }

        // Shared condition matcher: same parameter lookup and comparison rules filter_elements uses,
        // so "greater than 40000" means the same thing whichever tool the model reaches for.
        private static List<ElementId> MatchElements(Document doc, View view, BuiltInCategory bic,
            string paramName, string op, string value, out string paramHint)
        {
            paramHint = "";
            var hits = new List<ElementId>();
            double numeric;
            bool isNumeric = double.TryParse(value, out numeric);
            var seenParams = new HashSet<string>();

            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(bic).WhereElementIsNotElementType().Take(5000))
            {
                Parameter p = e.LookupParameter(paramName);
                if (p == null && e.GetTypeId() != ElementId.InvalidElementId)
                {
                    Element t = doc.GetElement(e.GetTypeId());
                    if (t != null) p = t.LookupParameter(paramName);
                }
                if (p == null)
                {
                    foreach (Parameter any in e.Parameters)
                        if (seenParams.Count < 40 && any.Definition != null) seenParams.Add(any.Definition.Name);
                    continue;
                }

                bool match = false;
                if (isNumeric && (p.StorageType == StorageType.Double || p.StorageType == StorageType.Integer))
                {
                    double actual = p.StorageType == StorageType.Double ? FtToMm(p.AsDouble()) : p.AsInteger();
                    switch (op)
                    {
                        case "greater": match = actual > numeric; break;
                        case "less": match = actual < numeric; break;
                        case "not_equals": match = Math.Abs(actual - numeric) > 1e-6; break;
                        default: match = Math.Abs(actual - numeric) < 1e-6; break;
                    }
                }
                else
                {
                    string actual = p.AsValueString() ?? p.AsString() ?? "";
                    switch (op)
                    {
                        case "contains": match = actual.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0; break;
                        case "not_equals": match = !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase); break;
                        default: match = string.Equals(actual, value, StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                if (match) hits.Add(e.Id);
            }

            if (hits.Count == 0 && seenParams.Count > 0)
                paramHint = " No element had a parameter called '" + paramName + "'. Available here: " +
                            string.Join(", ", seenParams.OrderBy(s => s).Take(20)) + ".";
            return hits;
        }

        private static object ColorByParameter(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            // This tool colours EVERY element grouped by a parameter, with automatic colours. If the
            // caller supplied a condition or a specific colour they wanted something else — say so
            // instead of silently ignoring those arguments and doing the wrong thing.
            if (args.ContainsKey("value") || args.ContainsKey("color") || args.ContainsKey("operator"))
                throw new InvalidOperationException(
                    "color_by_parameter colours ALL elements of a category grouped by a parameter, with " +
                    "automatic colours — it does not take 'value', 'operator' or 'color'. To colour only the " +
                    "elements matching a condition in a colour you choose, use color_elements_where, e.g. " +
                    "{\"category\":\"Walls\",\"parameter_name\":\"Unconnected Height\",\"operator\":\"greater\"," +
                    "\"value\":\"40000\",\"color\":\"yellow\"}.");

            View view = ResolveView(doc, args);
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            string paramName = ParamNameArg(args);
            if (paramName == null) throw new InvalidOperationException("'parameter_name' is required.");

            var valueColors = new Dictionary<string, int[]>();
            int colored = 0, next = 0;
            foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().Take(2000))
            {
                Parameter p = e.LookupParameter(paramName);
                if (p == null && e.GetTypeId() != ElementId.InvalidElementId)
                    p = doc.GetElement(e.GetTypeId())?.LookupParameter(paramName);
                string value = p != null ? (ParameterValueString(p) ?? "(empty)") : "(no parameter)";

                if (!valueColors.TryGetValue(value, out int[] rgb))
                {
                    rgb = Palette[next % Palette.Length]; next++;
                    valueColors[value] = rgb;
                }
                view.SetElementOverrides(e.Id, BuildColorOverride(doc, rgb[0], rgb[1], rgb[2], 0, false));
                colored++;
            }

            return new Dictionary<string, object>
            {
                { "colored", colored },
                { "view", view.Name },
                { "legend", valueColors.Select(kv => (object)new Dictionary<string, object>
                    { { "value", kv.Key }, { "rgb", new List<object> { kv.Value[0], kv.Value[1], kv.Value[2] } } }).ToList() }
            };
        }

        internal static object TagElements(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            View view = ResolveView(doc, args);
            ICollection<ElementId> ids = RequireIds(doc, args);
            int tagged = 0;
            var errors = new List<string>();
            foreach (ElementId id in ids)
            {
                try
                {
                    Element e = doc.GetElement(id);
                    XYZ pos = null;
                    var lp = e.Location as LocationPoint;
                    var lc = e.Location as LocationCurve;
                    if (lp != null) pos = lp.Point;
                    else if (lc != null) pos = lc.Curve.Evaluate(0.5, true);
                    else
                    {
                        BoundingBoxXYZ bb = e.get_BoundingBox(view);
                        if (bb != null) pos = (bb.Min + bb.Max) / 2;
                    }
                    if (pos == null) { errors.Add(id.IntegerValue + ": no location"); continue; }
                    IndependentTag.Create(doc, view.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, pos);
                    tagged++;
                }
                catch (Exception ex) { errors.Add(id.IntegerValue + ": " + ex.Message); }
            }
            var result = new Dictionary<string, object> { { "tagged", tagged }, { "view", view.Name } };
            if (errors.Count > 0) result["errors"] = errors.Take(10).Cast<object>().ToList();
            if (tagged == 0 && errors.Count > 0)
                throw new InvalidOperationException("No tags created. Is a tag family loaded for this category? First error: " + errors[0]);
            return result;
        }

        // ---------- create (v2) ----------

        private static object CreateRoom(Document doc, Dictionary<string, object> args)
        {
            Level level = ResolveLevel(doc, args);
            XYZ pt = PointFromArg(args, "location", 2);
            Room room = doc.Create.NewRoom(level, new UV(pt.X, pt.Y));
            string name = Json.GetString(args, "name");
            string number = Json.GetString(args, "number");
            if (!string.IsNullOrEmpty(name)) room.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(name);
            if (!string.IsNullOrEmpty(number)) { try { room.Number = number; } catch { } }
            bool placed = room.Area > 1e-9;
            return new Dictionary<string, object>
            {
                { "id", room.Id.IntegerValue },
                { "level", level.Name },
                { "area_m2", Math.Round(room.Area * SqFtToSqM, 2) },
                { "enclosed", placed },
                { "note", placed ? null : "Room created but the point is not inside an enclosed boundary." }
            };
        }

        private static object CreateCeiling(Document doc, Dictionary<string, object> args)
        {
            List<object> points = Json.GetList(args, "points");
            if (points == null || points.Count < 3)
                throw new InvalidOperationException("'points' must be an array of at least 3 [x,y] points in mm.");
            Level level = ResolveLevel(doc, args);
            double offset = MmToFt(Json.GetDouble(args, "height_offset_mm") ?? 2800.0);

            var xyz = points.Select(p =>
            {
                List<object> pt = Json.ToList(p);
                return new XYZ(MmToFt(Json.ToDouble(pt[0])), MmToFt(Json.ToDouble(pt[1])), level.Elevation);
            }).ToList();
            var loop = new CurveLoop();
            for (int i = 0; i < xyz.Count; i++)
            {
                XYZ a = xyz[i], b = xyz[(i + 1) % xyz.Count];
                if (a.DistanceTo(b) < 0.01) continue;
                loop.Append(Line.CreateBound(a, b));
            }

            ElementId typeId = ResolveTypeId(doc, args, ElementTypeGroup.CeilingType);
            Ceiling ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, typeId, level.Id);
            ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(offset);
            return new Dictionary<string, object>
            {
                { "id", ceiling.Id.IntegerValue },
                { "level", level.Name },
                { "height_offset_mm", Math.Round(FtToMm(offset), 1) }
            };
        }

        private static FamilySymbol FirstSymbolOf(Document doc, BuiltInCategory bic)
        {
            return new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
                .OfType<FamilySymbol>().FirstOrDefault();
        }

        private static object CreateColumn(Document doc, Dictionary<string, object> args)
        {
            Level level = ResolveLevel(doc, args);
            XYZ pt = PointFromArg(args, "location", 2);
            double height = MmToFt(Json.GetDouble(args, "height_mm") ?? 3000.0);

            FamilySymbol symbol = null;
            int? typeId = Json.GetInt(args, "type_id");
            if (typeId.HasValue) symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
            else symbol = FirstSymbolOf(doc, BuiltInCategory.OST_StructuralColumns) ?? FirstSymbolOf(doc, BuiltInCategory.OST_Columns);
            if (symbol == null)
                throw new InvalidOperationException("No column family type found. Load a column family or pass type_id (see list_element_types with category StructuralColumns or Columns).");
            if (!symbol.IsActive) symbol.Activate();

            var placePoint = new XYZ(pt.X, pt.Y, level.Elevation);
            FamilyInstance column = doc.Create.NewFamilyInstance(placePoint, symbol, level, StructuralType.Column);
            column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(level.Id);
            column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(height);
            return new Dictionary<string, object>
            {
                { "id", column.Id.IntegerValue },
                { "type", symbol.Name },
                { "level", level.Name },
                { "height_mm", Math.Round(FtToMm(height), 1) }
            };
        }

        private static object CreateBeam(Document doc, Dictionary<string, object> args)
        {
            Level level = ResolveLevel(doc, args);
            XYZ start = PointFromArg(args, "start", 2);
            XYZ end = PointFromArg(args, "end", 2);
            double z = MmToFt(Json.GetDouble(args, "elevation_mm") ?? FtToMm(level.Elevation));
            start = new XYZ(start.X, start.Y, z);
            end = new XYZ(end.X, end.Y, z);

            FamilySymbol symbol = null;
            int? typeId = Json.GetInt(args, "type_id");
            if (typeId.HasValue) symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
            else symbol = FirstSymbolOf(doc, BuiltInCategory.OST_StructuralFraming);
            if (symbol == null)
                throw new InvalidOperationException("No structural framing family loaded. Load a beam family or pass type_id (category StructuralFraming).");
            if (!symbol.IsActive) symbol.Activate();

            FamilyInstance beam = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, level, StructuralType.Beam);
            return new Dictionary<string, object>
            {
                { "id", beam.Id.IntegerValue },
                { "type", symbol.Name },
                { "length_mm", Math.Round(FtToMm(start.DistanceTo(end)), 1) }
            };
        }

        private static object CreateOpening(Document doc, Dictionary<string, object> args)
        {
            Element host = RequireElement(doc, args, "host_id");

            if (host is Wall wall)
            {
                XYZ p1 = PointFromArg(args, "corner1", 3);
                XYZ p2 = PointFromArg(args, "corner2", 3);
                Opening opening = doc.Create.NewOpening(wall, p1, p2);
                return new Dictionary<string, object> { { "id", opening.Id.IntegerValue }, { "host", "wall " + wall.Id.IntegerValue } };
            }

            List<object> points = Json.GetList(args, "points");
            if (points == null || points.Count < 3)
                throw new InvalidOperationException("For floors/ceilings/roofs pass 'points' ([[x,y],...] in mm); for walls pass 'corner1' and 'corner2' ([x,y,z] in mm).");
            double zHost = 0;
            BoundingBoxXYZ bb = host.get_BoundingBox(null);
            if (bb != null) zHost = bb.Max.Z;
            var arr = new CurveArray();
            var xyz = points.Select(p =>
            {
                List<object> pt = Json.ToList(p);
                return new XYZ(MmToFt(Json.ToDouble(pt[0])), MmToFt(Json.ToDouble(pt[1])), zHost);
            }).ToList();
            for (int i = 0; i < xyz.Count; i++)
            {
                XYZ a = xyz[i], b = xyz[(i + 1) % xyz.Count];
                if (a.DistanceTo(b) < 0.01) continue;
                arr.Append(Line.CreateBound(a, b));
            }
            Opening op = doc.Create.NewOpening(host, arr, true);
            return new Dictionary<string, object> { { "id", op.Id.IntegerValue }, { "host", host.Category?.Name + " " + host.Id.IntegerValue } };
        }

        // ---------- modify (v2) ----------

        private static object MirrorElements(Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            XYZ p1 = PointFromArg(args, "axis_start", 2);
            XYZ p2 = PointFromArg(args, "axis_end", 2);
            XYZ dir = (p2 - p1);
            if (dir.GetLength() < 1e-9) throw new InvalidOperationException("axis_start and axis_end must differ.");
            dir = dir.Normalize();
            var normal = new XYZ(-dir.Y, dir.X, 0);
            Plane plane = Plane.CreateByNormalAndOrigin(normal, p1);
            bool copy = !(args.ContainsKey("move") && args["move"] is bool mv && mv);
            IList<ElementId> newIds = ElementTransformUtils.MirrorElements(doc, ids, plane, copy);
            return new Dictionary<string, object>
            {
                { "mirrored", ids.Count },
                { "mode", copy ? "copied" : "moved" },
                { "new_ids", newIds != null ? newIds.Select(i => (object)i.IntegerValue).ToList() : new List<object>() }
            };
        }

        private static object SetParameterBulk(Document doc, Dictionary<string, object> args)
        {
            List<object> raw = Json.GetList(args, "element_ids");
            if (raw == null || raw.Count == 0) throw new InvalidOperationException("'element_ids' is required.");
            string paramName = ParamNameArg(args);
            if (paramName == null || !args.ContainsKey("value"))
                throw new InvalidOperationException(
                    MissingParamNameOrValue(paramName, args.ContainsKey("value") ? "set" : null));

            int ok = 0;
            var errors = new List<string>();
            foreach (object o in raw)
            {
                try
                {
                    var single = new Dictionary<string, object>
                    {
                        { "element_id", o }, { "parameter_name", paramName }, { "value", args["value"] }
                    };
                    SetParameter(doc, single);
                    ok++;
                }
                catch (Exception ex) { errors.Add(Json.ToInt(o) + ": " + ex.Message); }
            }
            var result = new Dictionary<string, object> { { "updated", ok }, { "failed", errors.Count } };
            if (errors.Count > 0) result["errors"] = errors.Take(10).Cast<object>().ToList();
            return result;
        }

        private static object RenameElement(Document doc, Dictionary<string, object> args)
        {
            Element e = RequireElement(doc, args, "element_id");
            string name = Json.GetString(args, "name");
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("'name' is required.");
            e.Name = name;
            return new Dictionary<string, object> { { "id", e.Id.IntegerValue }, { "name", e.Name } };
        }

        private static object JoinGeometry(Document doc, Dictionary<string, object> args)
        {
            Element a = RequireElement(doc, args, "element_id_a");
            Element b = RequireElement(doc, args, "element_id_b");
            string action = (Json.GetString(args, "action") ?? "join").ToLowerInvariant();
            if (action == "unjoin")
            {
                JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                return new Dictionary<string, object> { { "status", "unjoined" } };
            }
            JoinGeometryUtils.JoinGeometry(doc, a, b);
            return new Dictionary<string, object> { { "status", "joined" } };
        }

        // ---------- exports & files ----------

        private static object ExportPdf(Document doc, Dictionary<string, object> args)
        {
            var viewIds = new List<ElementId>();
            List<object> rawIds = Json.GetList(args, "view_ids");
            if (rawIds != null)
                foreach (object o in rawIds) viewIds.Add(new ElementId(Json.ToInt(o)));
            else if (doc.ActiveView != null) viewIds.Add(doc.ActiveView.Id);
            if (viewIds.Count == 0) throw new InvalidOperationException("No views to export.");

            string folder = ExportFolder();
            string fileName = Json.GetString(args, "file_name") ?? (SanitizeFileName(doc.Title) + "_export");
            var options = new PDFExportOptions { Combine = true, FileName = fileName };
            doc.Export(folder, viewIds, options);
            return new Dictionary<string, object>
            {
                { "file", Path.Combine(folder, fileName + ".pdf") },
                { "views_exported", viewIds.Count }
            };
        }

        private static object ExportIfc(Document doc, Dictionary<string, object> args)
        {
            string folder = ExportFolder();
            string fileName = (Json.GetString(args, "file_name") ?? SanitizeFileName(doc.Title)) + ".ifc";
            doc.Export(folder, fileName, new IFCExportOptions());
            return new Dictionary<string, object> { { "file", Path.Combine(folder, fileName) } };
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static object SaveDocument(Document doc)
        {
            if (string.IsNullOrEmpty(doc.PathName))
                throw new InvalidOperationException("This document has never been saved. Save it manually in Revit first (Save As).");
            doc.Save();
            return new Dictionary<string, object> { { "saved", doc.PathName } };
        }

        private static object LoadFamily(Document doc, Dictionary<string, object> args)
        {
            string path = Json.GetString(args, "path");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("'path' must be an existing .rfa file path.");
            Family family;
            if (!doc.LoadFamily(path, out family))
                throw new InvalidOperationException("Revit refused to load this family (already loaded with same version, or invalid file).");
            var types = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id))
                .OfType<FamilySymbol>()
                .Select(s => (object)new Dictionary<string, object> { { "id", s.Id.IntegerValue }, { "type", s.Name } })
                .ToList();
            return new Dictionary<string, object>
            {
                { "family", family.Name },
                { "types", types }
            };
        }

        // ---------- run_code: the escape hatch ----------

        private static object RunCode(UIApplication app, Document doc, Dictionary<string, object> args)
        {
            if (!AiconRoutineSettings.Load().AllowRunCode)
                throw new InvalidOperationException(
                    "run_code is turned off on this machine. Set \"allowRunCode\": true in " +
                    AiconRoutineSettings.FilePath + " and restart Revit to re-enable it.");

            string code = Json.GetString(args, "code");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("'code' is required.");
            bool useTransaction = !(args.ContainsKey("no_transaction") && args["no_transaction"] is bool nt && nt);

            string source =
                "using System;\n" +
                "using System.Collections.Generic;\n" +
                "using System.Linq;\n" +
                "using Autodesk.Revit.DB;\n" +
                "using Autodesk.Revit.DB.Architecture;\n" +
                "using Autodesk.Revit.DB.Structure;\n" +
                "using Autodesk.Revit.DB.Mechanical;\n" +
                "using Autodesk.Revit.DB.Plumbing;\n" +
                "using Autodesk.Revit.DB.Electrical;\n" +
                "using Autodesk.Revit.UI;\n" +
                "namespace AIConDynamic {\n" +
                "  public static class Script {\n" +
                "    public static object Run(UIApplication app, UIDocument uidoc, Document doc) {\n" +
                code + "\n" +
                "    }\n" +
                "  }\n" +
                "}\n";

            using (var provider = new CSharpCodeProvider())
            {
                var cp = new CompilerParameters { GenerateInMemory = true, TreatWarningsAsErrors = false };
                cp.ReferencedAssemblies.Add("System.dll");
                cp.ReferencedAssemblies.Add("System.Core.dll");
                cp.ReferencedAssemblies.Add("System.Xml.dll");
                cp.ReferencedAssemblies.Add(typeof(Document).Assembly.Location);       // RevitAPI
                cp.ReferencedAssemblies.Add(typeof(UIApplication).Assembly.Location);  // RevitAPIUI

                CompilerResults results = provider.CompileAssemblyFromSource(cp, source);
                if (results.Errors.HasErrors)
                {
                    var sb = new StringBuilder("C# compile errors (note: compiler supports C# 5 — no string interpolation ($\"\"), no ?. operator, no 'out var'):\n");
                    foreach (CompilerError err in results.Errors)
                        if (!err.IsWarning)
                            sb.AppendLine("line " + Math.Max(1, err.Line - 13) + ": " + err.ErrorText);
                    throw new InvalidOperationException(sb.ToString());
                }

                var method = results.CompiledAssembly.GetType("AIConDynamic.Script").GetMethod("Run");
                UIDocument uidoc = app.ActiveUIDocument;

                Func<object> invoke = () =>
                {
                    try { return method.Invoke(null, new object[] { app, uidoc, doc }); }
                    catch (System.Reflection.TargetInvocationException tie)
                    {
                        throw new InvalidOperationException("Code threw: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
                    }
                };

                object result = useTransaction
                    ? InTransaction(doc, "AICon: run_code", invoke)
                    : invoke();

                // Make sure whatever came back can survive JSON serialization.
                try { Json.Serialize(result); return result; }
                catch { return result != null ? result.ToString() : null; }
            }
        }
    }
}
