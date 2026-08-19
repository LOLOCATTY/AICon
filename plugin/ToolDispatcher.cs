using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace AICon
{
    /// <summary>
    /// Executes named tools against the active Revit document.
    /// All lengths/coordinates cross the wire in MILLIMETERS; Revit internal units are feet.
    /// </summary>
    internal static partial class ToolDispatcher
    {
        private const double MmPerFoot = 304.8;
        private const double SqFtToSqM = 0.09290304;
        private static double MmToFt(double mm) { return mm / MmPerFoot; }
        private static double FtToMm(double ft) { return ft * MmPerFoot; }

        public static object Dispatch(UIApplication app, string tool, Dictionary<string, object> args)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
                throw new InvalidOperationException("No Revit document is open. Open a project in Revit first.");
            Document doc = uidoc.Document;

            if (tool == "batch") return RunBatch(app, uidoc, doc, args);
            if (Mutating.Contains(tool))
                return InTransaction(doc, "AICon: " + tool.Replace('_', ' '), () => ExecuteCore(app, uidoc, doc, tool, args));
            return ExecuteCore(app, uidoc, doc, tool, args);
        }

        /// <summary>Tools that modify the document and therefore need a transaction.</summary>
        private static readonly HashSet<string> Mutating = new HashSet<string>
        {
            "set_parameter", "set_element_type", "set_parameter_bulk", "rename_element", "join_geometry",
            "create_wall", "create_floor", "create_level", "create_grid", "create_text_note",
            "place_family_instance", "create_room", "create_ceiling", "create_column", "create_beam",
            "create_opening", "move_elements", "copy_elements", "rotate_elements", "mirror_elements",
            "delete_elements", "create_floor_plan", "create_3d_view", "duplicate_view", "create_view_template", "create_view_filter", "apply_view_template",
            "create_schedule", "create_section", "create_dimension", "create_wall_dimension", "create_revision",
            "batch_rename", "create_elevation", "create_sheet",
            "place_view_on_sheet", "hide_elements", "unhide_elements", "isolate_elements", "reset_isolate",
            "override_element_color", "color_by_parameter", "color_elements_where", "tag_elements", "load_family", "export_ifc"
        };

        /// <summary>True when the tool modifies the model — used by the panel's read-only mode.</summary>
        internal static bool IsMutating(string tool)
        {
            return tool == "run_code" || tool == "batch" || Mutating.Contains(tool);
        }

        /// <summary>Tools that are not allowed inside a batch (they manage transactions/exports themselves).</summary>
        private static readonly HashSet<string> NotBatchable = new HashSet<string>
        {
            "batch", "run_code", "export_pdf", "export_ifc", "export_view_image", "save_document"
        };

        /// <summary>Runs one tool WITHOUT opening a transaction (the caller decides the transaction strategy).</summary>
        private static object ExecuteCore(UIApplication app, UIDocument uidoc, Document doc, string tool, Dictionary<string, object> args)
        {
            switch (tool)
            {
                // read
                case "get_project_info": return GetProjectInfo(uidoc, doc);
                case "list_levels": return ListLevels(doc);
                case "list_views": return ListViews(doc);
                case "list_rooms": return ListRooms(doc);
                case "list_categories": return ListCategories(doc);
                case "list_elements": return ListElements(doc, args);
                case "list_element_types": return ListElementTypes(doc, args);
                case "get_element": return GetElement(doc, args);
                case "get_selection": return GetSelection(uidoc, doc);
                case "get_warnings": return GetWarnings(doc, args);
                case "export_view_image": return ExportViewImage(doc, args);
                case "look_at_view": return ExportViewImage(doc, args);   // agent feeds the PNG back to the model
                case "list_sheets": return ListSheets(doc);
                case "get_schedule_data": return GetScheduleData(doc, args);
                case "filter_elements": return FilterElements(doc, args);
                case "quantities_by_type": return QuantitiesByType(doc, args);
                case "detect_clashes": return DetectClashes(doc, args);
                case "get_view_elements": return GetViewElements(doc, args);
                case "measure_between_elements": return MeasureBetweenElements(doc, args);
                case "__snapshot": return ProjectSnapshot(uidoc, doc);   // internal: chat-panel context

                // UI
                case "select_elements": return SelectElements(uidoc, doc, args);
                case "show_elements": return ShowElements(uidoc, doc, args);
                case "set_active_view": return SetActiveView(uidoc, doc, args);

                // views & documentation
                case "create_floor_plan": return CreateFloorPlan(doc, args);
                case "create_3d_view": return Create3DView(doc, args);
                case "duplicate_view": return DuplicateView(doc, args);
                case "create_view_template": return CreateViewTemplate(doc, args);
                case "create_view_filter": return CreateViewFilter(doc, args);
                case "apply_view_template": return ApplyViewTemplate(doc, args);
                case "create_schedule": return CreateSchedule(doc, args);
                case "create_section": return CreateSection(doc, args);
                case "create_dimension": return CreateDimension(doc, args);
                case "create_wall_dimension": return CreateWallDimension(doc, args);
                case "create_revision": return CreateRevision(doc, args);
                case "batch_rename": return BatchRename(doc, args);
                case "create_elevation": return CreateElevation(doc, args);
                case "create_sheet": return CreateSheet(doc, args);
                case "place_view_on_sheet": return PlaceViewOnSheet(doc, args);
                case "hide_elements": return HideElements(uidoc, doc, args, true);
                case "unhide_elements": return HideElements(uidoc, doc, args, false);
                case "isolate_elements": return IsolateElements(uidoc, doc, args);
                case "reset_isolate": return ResetIsolate(uidoc, doc, args);
                case "override_element_color": return OverrideElementColor(uidoc, doc, args);
                case "color_by_parameter": return ColorByParameter(uidoc, doc, args);
                case "color_elements_where": return ColorElementsWhere(uidoc, doc, args);

                // routines (saved, reusable capabilities)
                case "list_routines": return ListRoutines(args);
                case "save_routine": return SaveRoutine(args);
                case "run_routine": return RunRoutine(app, args);
                case "get_authoring_guide": return GetAuthoringGuide(args);
                case "tag_elements": return TagElements(uidoc, doc, args);

                // exports & file
                case "export_pdf": return ExportPdf(doc, args);
                case "export_ifc": return ExportIfc(doc, args);
                case "save_document": return SaveDocument(doc);
                case "load_family": return LoadFamily(doc, args);

                // write
                case "set_parameter": return SetParameter(doc, args);
                case "set_element_type": return SetElementType(doc, args);
                case "create_wall": return CreateWall(doc, args);
                case "create_floor": return CreateFloor(doc, args);
                case "create_level": return CreateLevel(doc, args);
                case "create_grid": return CreateGrid(doc, args);
                case "create_text_note": return CreateTextNote(uidoc, doc, args);
                case "place_family_instance": return PlaceFamilyInstance(doc, args);
                case "move_elements": return MoveElements(doc, args);
                case "copy_elements": return CopyElements(doc, args);
                case "rotate_elements": return RotateElements(doc, args);
                case "mirror_elements": return MirrorElements(doc, args);
                case "delete_elements": return DeleteElements(doc, args);
                case "set_parameter_bulk": return SetParameterBulk(doc, args);
                case "rename_element": return RenameElement(doc, args);
                case "join_geometry": return JoinGeometry(doc, args);
                case "create_room": return CreateRoom(doc, args);
                case "create_ceiling": return CreateCeiling(doc, args);
                case "create_column": return CreateColumn(doc, args);
                case "create_beam": return CreateBeam(doc, args);
                case "create_opening": return CreateOpening(doc, args);
                case "run_code": return RunCode(app, doc, args); // manages its own transaction
                default:
                    throw new InvalidOperationException("Unknown tool: " + tool + "." + SuggestTools(tool));
            }
        }

        // "apply_filter" → " Did you mean: create_view_filter, apply_view_template?" — models invent
        // near-miss tool names; pointing at the real ones lets them self-correct next round.
        private static string SuggestTools(string unknown)
        {
            if (AICon.Shared.Tools.Names.Count == 0) AICon.Shared.Tools.BuildToolList();
            var tokens = unknown.ToLowerInvariant().Split('_').Where(t => t.Length > 2).ToList();
            var ranked = AICon.Shared.Tools.Names
                .Select(n => new { n, score = tokens.Count(t => n.Contains(t)) })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(3).Select(x => x.n).ToList();
            return ranked.Count > 0
                ? " Did you mean: " + string.Join(", ", ranked) + "? Only listed tools exist."
                : " Only the listed tools exist — do not invent tool names.";
        }

        /// <summary>
        /// Executes many operations in ONE call and ONE transaction — the fast path for bulk work
        /// (e.g. 36 plans + 36 sheets + 36 viewports in a single round-trip instead of 108).
        /// </summary>
        private static object RunBatch(UIApplication app, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            List<object> ops = Json.GetList(args, "operations");
            if (ops == null || ops.Count == 0)
                throw new InvalidOperationException("'operations' must be a non-empty array of {tool, args} objects.");
            bool continueOnError = args.ContainsKey("continue_on_error") && args["continue_on_error"] is bool c && c;

            var results = new List<object>();
            int ok = 0, failed = 0;

            return InTransaction(doc, "AICon: batch (" + ops.Count + " ops)", () =>
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    var op = ops[i] as Dictionary<string, object>;
                    string opTool = op != null ? Json.GetString(op, "tool") : null;
                    if (string.IsNullOrEmpty(opTool))
                        throw new InvalidOperationException("Operation " + (i + 1) + " is missing 'tool'.");
                    if (NotBatchable.Contains(opTool))
                        throw new InvalidOperationException("'" + opTool + "' cannot run inside batch — call it separately.");

                    try
                    {
                        object data = ExecuteCore(app, uidoc, doc, opTool, Json.GetDict(op, "args") ?? new Dictionary<string, object>());
                        results.Add(new Dictionary<string, object> { { "op", i + 1 }, { "tool", opTool }, { "ok", true }, { "data", data } });
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        string message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                        failed++;
                        results.Add(new Dictionary<string, object> { { "op", i + 1 }, { "tool", opTool }, { "ok", false }, { "error", message } });
                        if (!continueOnError)
                            throw new InvalidOperationException(
                                "Batch stopped at operation " + (i + 1) + " (" + opTool + "): " + message +
                                ". Nothing was committed (all-or-nothing). Fix the operation and resend, or use continue_on_error=true.");
                    }
                }
                return new Dictionary<string, object>
                {
                    { "total", ops.Count },
                    { "succeeded", ok },
                    { "failed", failed },
                    { "results", results }
                };
            });
        }

        private static object InTransaction(Document doc, string name, Func<object> action)
        {
            using (var t = new Transaction(doc, name))
            {
                t.Start();
                try
                {
                    object result = action();
                    t.Commit();
                    return result;
                }
                catch
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    throw;
                }
            }
        }

        // ---------- read tools ----------

        private static object GetProjectInfo(UIDocument uidoc, Document doc)
        {
            ProjectInfo pi = doc.ProjectInformation;
            View view = doc.ActiveView;
            return new Dictionary<string, object>
            {
                { "title", doc.Title },
                { "path", doc.PathName },
                { "project_name", pi != null ? pi.Name : null },
                { "project_number", pi != null ? pi.Number : null },
                { "client", pi != null ? pi.ClientName : null },
                { "active_view", view != null ? view.Name : null },
                { "active_view_id", view != null ? (object)view.Id.ToInt() : null },
                { "active_view_type", view != null ? view.ViewType.ToString() : null },
                { "units_note", "All tool inputs/outputs use millimeters (areas in m2)." },
                { "selection_count", uidoc.Selection.GetElementIds().Count },
                { "warning_count", doc.GetWarnings().Count }
            };
        }

        private static object ListLevels(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (object)new Dictionary<string, object>
                {
                    { "id", l.Id.ToInt() },
                    { "name", l.Name },
                    { "elevation_mm", Math.Round(FtToMm(l.Elevation), 1) }
                }).ToList();
        }

        private static object ListViews(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                .Select(v => (object)new Dictionary<string, object>
                {
                    { "id", v.Id.ToInt() },
                    { "name", v.Name },
                    { "type", v.ViewType.ToString() }
                }).ToList();
        }

        private static object ListRooms(Document doc)
        {
            var result = new List<object>();
            foreach (Room room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                         .OfType<Room>())
            {
                bool placed = room.Location != null && room.Area > 1e-9;
                result.Add(new Dictionary<string, object>
                {
                    { "id", room.Id.ToInt() },
                    { "number", room.Number },
                    { "name", room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name },
                    { "level", room.Level != null ? room.Level.Name : null },
                    { "area_m2", placed ? (object)Math.Round(room.Area * SqFtToSqM, 2) : null },
                    { "placed", placed }
                });
            }
            return result;
        }

        private static object ListCategories(Document doc)
        {
            var counts = new Dictionary<string, int>();
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            foreach (Element e in collector)
            {
                if (e.Category == null) continue;
                string name = e.Category.Name;
                counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
            }
            return counts.OrderByDescending(kv => kv.Value)
                .Select(kv => (object)new Dictionary<string, object> { { "category", kv.Key }, { "count", kv.Value } })
                .ToList();
        }

        private static BuiltInCategory ParseCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("A 'category' is required, e.g. Walls, Doors, Windows, Floors, Rooms.");
            string compact = name.Replace(" ", "");
            if (Enum.TryParse("OST_" + compact, true, out BuiltInCategory bic)) return bic;
            if (Enum.TryParse(compact, true, out bic)) return bic;
            throw new InvalidOperationException(
                "Unknown category '" + name + "'. Use names like Walls, Doors, Windows, Floors, Ceilings, Rooms, StructuralColumns, StructuralFraming, GenericModel.");
        }

        private static object ListElements(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            int limit = Json.GetInt(args, "limit") ?? 100;

            // GetElementCount() for the total (no Element wrappers built for anything beyond what's
            // returned), then a fresh collector capped at 'limit' for the sample — avoids materializing
            // the full ICollection<Element> just to report a count and throw most of it away, which
            // matters on a category with thousands of instances.
            int total = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().GetElementCount();
            var sample = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().Take(limit);

            var result = new List<object>();
            foreach (Element e in sample)
            {
                Element type = e.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(e.GetTypeId()) : null;
                Level level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId) as Level : null;
                result.Add(new Dictionary<string, object>
                {
                    { "id", e.Id.ToInt() },
                    { "name", e.Name },
                    { "type", type != null ? type.Name : null },
                    { "level", level != null ? level.Name : null }
                });
            }
            return new Dictionary<string, object>
            {
                { "total_in_model", total },
                { "returned", result.Count },
                { "elements", result }
            };
        }

        private static object ListElementTypes(Document doc, Dictionary<string, object> args)
        {
            BuiltInCategory bic = ParseCategory(Json.GetString(args, "category"));
            string contains = Json.GetString(args, "name_contains");

            var types = new FilteredElementCollector(doc)
                .OfCategory(bic).WhereElementIsElementType().Cast<ElementType>();

            var result = new List<object>();
            foreach (ElementType t in types)
            {
                string family = t.FamilyName;
                if (contains != null &&
                    (t.Name + " " + family).IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                result.Add(new Dictionary<string, object>
                {
                    { "id", t.Id.ToInt() },
                    { "family", family },
                    { "type", t.Name }
                });
            }
            return result;
        }

        private static object GetElement(Document doc, Dictionary<string, object> args)
        {
            Element e = RequireElement(doc, args, "element_id");

            var parameters = new List<object>();
            foreach (Parameter p in e.Parameters)
            {
                if (p.Definition == null) continue;
                parameters.Add(new Dictionary<string, object>
                {
                    { "name", p.Definition.Name },
                    { "value", ParameterValueString(p) },
                    { "storage", p.StorageType.ToString() },
                    { "read_only", p.IsReadOnly }
                });
            }

            Element typeElem = e.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(e.GetTypeId()) : null;
            var info = new Dictionary<string, object>
            {
                { "id", e.Id.ToInt() },
                { "name", e.Name },
                { "category", e.Category != null ? e.Category.Name : null },
                { "type_id", typeElem != null ? (object)typeElem.Id.ToInt() : null },
                { "type_name", typeElem != null ? typeElem.Name : null },
                { "parameters", parameters.OrderBy(p2 => (string)((Dictionary<string, object>)p2)["name"]).ToList() }
            };

            var lp = e.Location as LocationPoint;
            var lc = e.Location as LocationCurve;
            if (lp != null) info["location_mm"] = XyzToMm(lp.Point);
            else if (lc != null)
            {
                info["location_start_mm"] = XyzToMm(lc.Curve.GetEndPoint(0));
                info["location_end_mm"] = XyzToMm(lc.Curve.GetEndPoint(1));
            }

            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                info["bbox_min_mm"] = XyzToMm(bb.Min);
                info["bbox_max_mm"] = XyzToMm(bb.Max);
            }
            return info;
        }

        private static object GetSelection(UIDocument uidoc, Document doc)
        {
            var result = new List<object>();
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "id", id.ToInt() },
                    { "name", e.Name },
                    { "category", e.Category != null ? e.Category.Name : null }
                });
            }
            return result;
        }

        private static object GetWarnings(Document doc, Dictionary<string, object> args)
        {
            int limit = Json.GetInt(args, "limit") ?? 50;
            IList<FailureMessage> warnings = doc.GetWarnings();
            var result = new List<object>();
            foreach (FailureMessage w in warnings.Take(limit))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "description", w.GetDescriptionText() },
                    { "element_ids", w.GetFailingElements().Select(id => (object)id.ToInt()).ToList() }
                });
            }
            return new Dictionary<string, object>
            {
                { "total_warnings", warnings.Count },
                { "returned", result.Count },
                { "warnings", result }
            };
        }

        private static object ExportViewImage(Document doc, Dictionary<string, object> args)
        {
            int width = Math.Max(256, Math.Min(2048, Json.GetInt(args, "width_px") ?? 1200));
            int? viewIdInt = Json.GetInt(args, "view_id");

            var options = new ImageExportOptions
            {
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = width,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ExportRange = ExportRange.VisibleRegionOfCurrentView
            };

            if (viewIdInt.HasValue)
            {
                var view = doc.GetElement(ElementIdCompat.FromInt(viewIdInt.Value)) as View;
                if (view == null || view.IsTemplate || !view.CanBePrinted)
                    throw new InvalidOperationException("view_id " + viewIdInt.Value + " is not an exportable view. Use list_views.");
                options.ExportRange = ExportRange.SetOfViews;
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
            }

            string baseName = "aicon_" + Guid.NewGuid().ToString("N");
            string basePath = Path.Combine(Path.GetTempPath(), baseName);
            options.FilePath = basePath;

            doc.ExportImage(options);

            // Revit appends view info to the file name; find what it produced.
            string[] files = Directory.GetFiles(Path.GetTempPath(), baseName + "*.png");
            if (files.Length == 0)
                throw new InvalidOperationException("Revit did not produce an image for this view.");

            try
            {
                byte[] bytes = File.ReadAllBytes(files[0]);
                return new Dictionary<string, object>
                {
                    { "image_base64", Convert.ToBase64String(bytes) },
                    { "format", "png" },
                    { "width_px", width }
                };
            }
            finally
            {
                foreach (string f in files) { try { File.Delete(f); } catch { } }
            }
        }

        // ---------- UI tools ----------

        private static object SelectElements(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            uidoc.Selection.SetElementIds(ids);
            return new Dictionary<string, object> { { "selected", ids.Count } };
        }

        private static object ShowElements(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            uidoc.ShowElements(ids);
            uidoc.Selection.SetElementIds(ids);
            return new Dictionary<string, object> { { "shown", ids.Count } };
        }

        private static string ParameterValueString(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsInteger().ToString();
                case StorageType.ElementId:
                    ElementId id = p.AsElementId();
                    return id != null ? id.ToInt().ToString() : null;
                case StorageType.Double:
                    string vs = p.AsValueString();
                    return vs ?? p.AsDouble().ToString("0.####");
                default: return null;
            }
        }

        // ---------- write tools ----------

        private static object SetParameter(Document doc, Dictionary<string, object> args)
        {
            Element e = RequireElement(doc, args, "element_id");
            string paramName = ParamNameArg(args);
            if (string.IsNullOrEmpty(paramName))
                throw new InvalidOperationException("'parameter_name' is required.");
            if (!args.ContainsKey("value"))
                throw new InvalidOperationException("'value' is required.");
            object value = args["value"];

            Parameter p = e.LookupParameter(paramName);
            string owner = "instance";
            if (p == null && e.GetTypeId() != ElementId.InvalidElementId)
            {
                Element type = doc.GetElement(e.GetTypeId());
                p = type != null ? type.LookupParameter(paramName) : null;
                owner = "type";
            }
            if (p == null)
                throw new InvalidOperationException("Parameter '" + paramName + "' not found on element " + e.Id.ToInt() + " or its type.");
            if (p.IsReadOnly)
                throw new InvalidOperationException("Parameter '" + paramName + "' is read-only.");

            switch (p.StorageType)
            {
                case StorageType.String:
                    p.Set(Convert.ToString(value));
                    break;
                case StorageType.Integer:
                    p.Set(Json.ToInt(value is bool b ? (b ? 1 : 0) : value));
                    break;
                case StorageType.ElementId:
                    p.Set(ElementIdCompat.FromInt(Json.ToInt(value)));
                    break;
                case StorageType.Double:
                    double raw = Json.ToDouble(value);
                    ForgeTypeId spec = p.Definition.GetDataType();
                    double internalValue;
                    if (spec == SpecTypeId.Length)
                        internalValue = MmToFt(raw); // input in mm
                    else if (spec == SpecTypeId.Angle)
                        internalValue = raw * Math.PI / 180.0; // input in degrees
                    else if (UnitUtils.IsMeasurableSpec(spec))
                        internalValue = UnitUtils.ConvertToInternalUnits(raw, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
                    else
                        internalValue = raw;
                    p.Set(internalValue);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported parameter storage type.");
            }

            return new Dictionary<string, object>
            {
                { "element_id", e.Id.ToInt() },
                { "parameter", paramName },
                { "owner", owner },
                { "new_value", ParameterValueString(p) }
            };
        }

        private static object SetElementType(Document doc, Dictionary<string, object> args)
        {
            Element e = RequireElement(doc, args, "element_id");
            int? typeIdInt = Json.GetInt(args, "type_id");
            if (!typeIdInt.HasValue)
                throw new InvalidOperationException("'type_id' is required (see list_element_types).");
            var newType = doc.GetElement(ElementIdCompat.FromInt(typeIdInt.Value)) as ElementType;
            if (newType == null)
                throw new InvalidOperationException("type_id " + typeIdInt.Value + " is not a valid element type.");
            e.ChangeTypeId(newType.Id);
            return new Dictionary<string, object>
            {
                { "element_id", e.Id.ToInt() },
                { "new_type", newType.Name },
                { "family", newType.FamilyName }
            };
        }

        private static object CreateWall(Document doc, Dictionary<string, object> args)
        {
            XYZ start = PointFromArg(args, "start", 2);
            XYZ end = PointFromArg(args, "end", 2);
            Level level = ResolveLevel(doc, args);
            double height = MmToFt(Json.GetDouble(args, "height_mm") ?? 3000.0);

            ElementId typeId = ResolveTypeId(doc, args, ElementTypeGroup.WallType);
            Line line = Line.CreateBound(start, end);
            Wall wall = Wall.Create(doc, line, typeId, level.Id, height, 0.0, false, false);

            return new Dictionary<string, object>
            {
                { "id", wall.Id.ToInt() },
                { "type", doc.GetElement(wall.WallType.Id).Name },
                { "level", level.Name },
                { "length_mm", Math.Round(FtToMm(line.Length), 1) }
            };
        }

        private static object CreateFloor(Document doc, Dictionary<string, object> args)
        {
            List<object> points = Json.GetList(args, "points");
            if (points == null || points.Count < 3)
                throw new InvalidOperationException("'points' must be an array of at least 3 [x,y] points in mm, e.g. [[0,0],[5000,0],[5000,4000],[0,4000]].");
            Level level = ResolveLevel(doc, args);
            ElementId typeId = ResolveTypeId(doc, args, ElementTypeGroup.FloorType);

            var xyz = points.Select(p =>
            {
                List<object> pt = Json.ToList(p);
                if (pt == null || pt.Count < 2) throw new InvalidOperationException("Each point must be [x,y] in mm.");
                return new XYZ(MmToFt(Json.ToDouble(pt[0])), MmToFt(Json.ToDouble(pt[1])), level.Elevation);
            }).ToList();

            var loop = new CurveLoop();
            for (int i = 0; i < xyz.Count; i++)
            {
                XYZ a = xyz[i], b = xyz[(i + 1) % xyz.Count];
                if (a.DistanceTo(b) < 0.01) continue;
                loop.Append(Line.CreateBound(a, b));
            }

            Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, typeId, level.Id);
            return new Dictionary<string, object>
            {
                { "id", floor.Id.ToInt() },
                { "level", level.Name },
                { "points_used", xyz.Count }
            };
        }

        private static object CreateLevel(Document doc, Dictionary<string, object> args)
        {
            double? elevation = Json.GetDouble(args, "elevation_mm");
            if (!elevation.HasValue)
                throw new InvalidOperationException("'elevation_mm' is required.");
            Level level = Level.Create(doc, MmToFt(elevation.Value));
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) level.Name = name;
            return new Dictionary<string, object>
            {
                { "id", level.Id.ToInt() },
                { "name", level.Name },
                { "elevation_mm", Math.Round(elevation.Value, 1) }
            };
        }

        private static object CreateGrid(Document doc, Dictionary<string, object> args)
        {
            XYZ start = PointFromArg(args, "start", 2);
            XYZ end = PointFromArg(args, "end", 2);
            Grid grid = Grid.Create(doc, Line.CreateBound(start, end));
            string name = Json.GetString(args, "name");
            if (!string.IsNullOrEmpty(name)) grid.Name = name;
            return new Dictionary<string, object> { { "id", grid.Id.ToInt() }, { "name", grid.Name } };
        }

        private static object CreateTextNote(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            string text = Json.GetString(args, "text");
            if (string.IsNullOrEmpty(text))
                throw new InvalidOperationException("'text' is required.");
            XYZ position = PointFromArg(args, "position", 2);

            View view = doc.ActiveView;
            int? viewIdInt = Json.GetInt(args, "view_id");
            if (viewIdInt.HasValue)
            {
                view = doc.GetElement(ElementIdCompat.FromInt(viewIdInt.Value)) as View;
                if (view == null) throw new InvalidOperationException("view_id " + viewIdInt.Value + " is not a view.");
            }

            ElementId typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            TextNote note = TextNote.Create(doc, view.Id, position, text, typeId);
            return new Dictionary<string, object>
            {
                { "id", note.Id.ToInt() },
                { "view", view.Name }
            };
        }

        private static object PlaceFamilyInstance(Document doc, Dictionary<string, object> args)
        {
            int? typeIdInt = Json.GetInt(args, "type_id");
            if (!typeIdInt.HasValue)
                throw new InvalidOperationException("'type_id' is required. Find one with list_element_types.");
            var symbol = doc.GetElement(ElementIdCompat.FromInt(typeIdInt.Value)) as FamilySymbol;
            if (symbol == null)
                throw new InvalidOperationException("type_id " + typeIdInt.Value + " is not a loadable family type. Use list_element_types to find valid ids.");
            if (!symbol.IsActive) symbol.Activate();

            XYZ location = PointFromArg(args, "location", 3);
            int? hostId = Json.GetInt(args, "host_id");

            FamilyInstance instance;
            if (hostId.HasValue)
            {
                Element host = doc.GetElement(ElementIdCompat.FromInt(hostId.Value));
                if (host == null) throw new InvalidOperationException("host_id " + hostId.Value + " not found.");
                Level level = ResolveLevelOrNull(doc, args) ?? (host.LevelId != ElementId.InvalidElementId ? doc.GetElement(host.LevelId) as Level : null);
                instance = doc.Create.NewFamilyInstance(location, symbol, host, level, StructuralType.NonStructural);
            }
            else
            {
                Level level = ResolveLevelOrNull(doc, args);
                instance = level != null
                    ? doc.Create.NewFamilyInstance(location, symbol, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(location, symbol, StructuralType.NonStructural);
            }

            return new Dictionary<string, object>
            {
                { "id", instance.Id.ToInt() },
                { "family", symbol.FamilyName },
                { "type", symbol.Name }
            };
        }

        private static object MoveElements(Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            XYZ translation = VectorFromArg(args, "vector_mm");
            ElementTransformUtils.MoveElements(doc, ids, translation);
            return new Dictionary<string, object> { { "moved", ids.Count } };
        }

        private static object CopyElements(Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            XYZ translation = VectorFromArg(args, "vector_mm");
            ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, ids, translation);
            return new Dictionary<string, object>
            {
                { "copied", ids.Count },
                { "new_ids", copies.Select(id => (object)id.ToInt()).ToList() }
            };
        }

        private static object RotateElements(Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            XYZ center = PointFromArg(args, "center", 2);
            double? angleDeg = Json.GetDouble(args, "angle_deg");
            if (!angleDeg.HasValue)
                throw new InvalidOperationException("'angle_deg' is required (counter-clockwise, degrees).");
            Line axis = Line.CreateBound(center, center + XYZ.BasisZ);
            ElementTransformUtils.RotateElements(doc, ids, axis, angleDeg.Value * Math.PI / 180.0);
            return new Dictionary<string, object> { { "rotated", ids.Count }, { "angle_deg", angleDeg.Value } };
        }

        private static object DeleteElements(Document doc, Dictionary<string, object> args)
        {
            ICollection<ElementId> ids = RequireIds(doc, args);
            // Deletion is undoable (Ctrl+Z) but otherwise unconfirmed on this path — the in-Revit chat
            // panel has its own Yes/No gate (InProcessToolExecutor.cs) before it even gets here; the
            // MCP/HTTP path (Claude Desktop) does not. Log unconditionally so there is at least a record
            // of what was deleted, from where, and when.
            App.Log("delete_elements: " + ids.Count + " requested (ids: " +
                    string.Join(",", ids.Take(20).Select(id => id.ToInt())) +
                    (ids.Count > 20 ? ", …" : "") + ")");
            ICollection<ElementId> deleted = doc.Delete(ids);
            return new Dictionary<string, object>
            {
                { "requested", ids.Count },
                { "deleted_including_dependents", deleted != null ? deleted.Count : 0 }
            };
        }

        // ---------- helpers ----------

        private static Element RequireElement(Document doc, Dictionary<string, object> args, string key)
        {
            int? id = Json.GetInt(args, key);
            if (!id.HasValue) throw new InvalidOperationException("'" + key + "' is required.");
            Element e = doc.GetElement(ElementIdCompat.FromInt(id.Value));
            if (e == null) throw new InvalidOperationException("Element " + id.Value + " not found in this model.");
            return e;
        }

        private static ICollection<ElementId> RequireIds(Document doc, Dictionary<string, object> args)
        {
            List<object> raw = Json.GetList(args, "element_ids");
            if (raw == null || raw.Count == 0)
                throw new InvalidOperationException("'element_ids' must be a non-empty array of ids.");
            var ids = new List<ElementId>();
            foreach (object o in raw)
            {
                var id = ElementIdCompat.FromInt(Json.ToInt(o));
                if (doc.GetElement(id) == null)
                    throw new InvalidOperationException("Element " + id.ToInt() + " not found in this model.");
                ids.Add(id);
            }
            return ids;
        }

        private static XYZ PointFromArg(Dictionary<string, object> args, string key, int minComponents)
        {
            List<object> pt = Json.GetList(args, key);
            if (pt == null || pt.Count < minComponents)
                throw new InvalidOperationException("'" + key + "' must be an array of " +
                    (minComponents == 2 ? "[x,y]" : "[x,y,z]") + " coordinates in mm.");
            return new XYZ(
                MmToFt(Json.ToDouble(pt[0])),
                MmToFt(Json.ToDouble(pt[1])),
                pt.Count > 2 ? MmToFt(Json.ToDouble(pt[2])) : 0);
        }

        private static XYZ VectorFromArg(Dictionary<string, object> args, string key)
        {
            List<object> vec = Json.GetList(args, key);
            if (vec == null || vec.Count < 2)
                throw new InvalidOperationException("'" + key + "' must be [x,y] or [x,y,z] in mm.");
            return new XYZ(
                MmToFt(Json.ToDouble(vec[0])),
                MmToFt(Json.ToDouble(vec[1])),
                vec.Count > 2 ? MmToFt(Json.ToDouble(vec[2])) : 0);
        }

        private static Level ResolveLevel(Document doc, Dictionary<string, object> args)
        {
            Level level = ResolveLevelOrNull(doc, args);
            if (level == null)
                throw new InvalidOperationException("Provide 'level' (a level name or id). Use list_levels to see them.");
            return level;
        }

        private static Level ResolveLevelOrNull(Document doc, Dictionary<string, object> args)
        {
            object raw;
            if (!args.TryGetValue("level", out raw) || raw == null) return null;

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();

            string asString = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            Level byName = levels.FirstOrDefault(l => string.Equals(l.Name, asString, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            if (int.TryParse(asString, out int idInt))
            {
                Level byId = levels.FirstOrDefault(l => l.Id.ToInt() == idInt);
                if (byId != null) return byId;
            }
            throw new InvalidOperationException("Level '" + asString + "' not found. Use list_levels to see available levels.");
        }

        private static ElementId ResolveTypeId(Document doc, Dictionary<string, object> args, ElementTypeGroup defaultGroup)
        {
            int? typeIdInt = Json.GetInt(args, "type_id");
            if (typeIdInt.HasValue)
            {
                var e = doc.GetElement(ElementIdCompat.FromInt(typeIdInt.Value)) as ElementType;
                if (e == null) throw new InvalidOperationException("type_id " + typeIdInt.Value + " is not a valid element type.");
                return e.Id;
            }
            ElementId defaultId = doc.GetDefaultElementTypeId(defaultGroup);
            if (defaultId == ElementId.InvalidElementId)
                throw new InvalidOperationException("No default type available; pass 'type_id' (see list_element_types).");
            return defaultId;
        }

        private static object XyzToMm(XYZ p)
        {
            return new List<object>
            {
                Math.Round(FtToMm(p.X), 1),
                Math.Round(FtToMm(p.Y), 1),
                Math.Round(FtToMm(p.Z), 1)
            };
        }
    }
}
