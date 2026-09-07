#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AICon.Shared;

// Single source of truth for AICon's tool catalogue (76 Revit tools — see BuildToolList below;
// don't hardcode this number elsewhere, README.md included, it has drifted before).
// Each tool is described as { name, description, inputSchema } where inputSchema is
// standard JSON Schema. This is emitted verbatim as MCP tools/list, and converted into
// OpenAI/Gemini function-declaration formats by the agent host. Do NOT duplicate this list.
public static class Tools
{
    public static readonly HashSet<string> Names = new();

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject PointProp(string description) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["description"] = description
        };

    private static JsonObject IdArrayProp(string description) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["description"] = description
        };

    private static JsonObject Tool(string name, string description,
        Dictionary<string, JsonNode>? props = null, string[]? required = null)
    {
        Names.Add(name);
        var properties = new JsonObject();
        if (props != null)
            foreach (var kv in props) properties[kv.Key] = kv.Value;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    public static JsonArray BuildToolList()
    {
        const string mm = "All lengths and coordinates are in MILLIMETERS.";
        return new JsonArray(
            // --- THE FAST PATH for bulk work ---
            Tool("batch",
                "FAST bulk execution: run MANY AICon operations in ONE call and ONE Revit transaction. " +
                "ALWAYS use this instead of repeated individual calls when creating or modifying more than ~3 things " +
                "(e.g. 36 create_floor_plan + 36 create_sheet + 36 place_view_on_sheet = one batch with 108 operations — seconds instead of many minutes). " +
                "Each operation is {tool, args} using the same names/args as the normal tools. " +
                "Operations run in order; later ops can NOT reference ids created by earlier ops in the same batch (ids are returned per-op in the result) — " +
                "so run e.g. one batch creating views, read their ids from the result, then a second batch creating sheets and placing those views. " +
                "All-or-nothing by default (one failure rolls everything back); set continue_on_error=true to keep going and commit the successes. " +
                "Not allowed inside batch: run_code, export_pdf, export_ifc, export_view_image, save_document.",
                new() {
                    ["operations"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered list of operations",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name, e.g. 'create_floor_plan'" },
                                ["args"] = new JsonObject { ["type"] = "object", ["description"] = "That tool's arguments" }
                            },
                            ["required"] = new JsonArray("tool")
                        }
                    },
                    ["continue_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = record failures and continue (default: stop and roll back everything)" }
                }, new[] { "operations" }),

            // --- read ---
            Tool("get_project_info",
                "Get info about the open Revit project: title, active view, selection count, warning count."),
            Tool("list_levels",
                "List all levels with ids, names, and elevations in mm."),
            Tool("list_views",
                "List printable views (floor plans, sections, elevations, 3D views...) with ids. Use with export_view_image."),
            Tool("list_rooms",
                "List all rooms: number, name, level, area in m2, and whether the room is placed."),
            Tool("list_categories",
                "List model element categories present in the project with instance counts. Good first step to discover what's in the model."),
            Tool("list_elements",
                "List element instances of a category (e.g. Walls, Doors, Windows, Floors, Rooms, StructuralColumns). Returns id, name, type, level.",
                new() {
                    ["category"] = Prop("string", "Category name, e.g. 'Walls', 'Doors', 'Windows', 'Floors', 'Rooms'"),
                    ["limit"] = Prop("number", "Max elements to return (default 100)")
                }, new[] { "category" }),
            Tool("list_element_types",
                "List available element types (wall types, door/window family types, etc.) for a category. Use the returned id as 'type_id' when creating or retyping elements.",
                new() {
                    ["category"] = Prop("string", "Category name, e.g. 'Walls', 'Doors', 'Windows', 'Floors'"),
                    ["name_contains"] = Prop("string", "Optional filter: only types whose family/type name contains this text")
                }, new[] { "category" }),
            Tool("get_element",
                $"Get full details of one element: all parameters, location, bounding box. {mm}",
                new() { ["element_id"] = Prop("number", "The element id") },
                new[] { "element_id" }),
            Tool("get_selection",
                "Get the elements the user currently has selected in Revit. Use when the user says 'this wall' or 'the selected elements'."),
            Tool("get_warnings",
                "List the model's review warnings (Revit's warning dialog content) with the ids of the offending elements.",
                new() { ["limit"] = Prop("number", "Max warnings to return (default 50)") }),
            Tool("export_view_image",
                "Render a view to an image so you can SEE the model. Defaults to the active view; pass view_id (from list_views) for another view.",
                new() {
                    ["view_id"] = Prop("number", "Optional view id (see list_views); default is the active view"),
                    ["width_px"] = Prop("number", "Image width in pixels, 256-2048 (default 1200)")
                }),

            // --- UI ---
            Tool("look_at_view",
                "SEE a view with your own eyes: exports the view (default: active view) as an image and the image " +
                "is then shown to you. Use it when the user asks about visual appearance, layout, colors, or " +
                "anything you must LOOK at to answer. Requires a vision-capable model (e.g. Gemini).",
                new() {
                    ["view_id"] = Prop("number", "View to look at (default: the active view)"),
                    ["width_px"] = Prop("number", "Image width in pixels 256–2048 (default 1200)")
                }),
            Tool("select_elements",
                "Select elements in the Revit UI so the user can see which ones you mean.",
                new() { ["element_ids"] = IdArrayProp("Ids of elements to select") },
                new[] { "element_ids" }),
            Tool("show_elements",
                "Zoom the Revit view to given elements and select them. Great for 'where is it?' questions.",
                new() { ["element_ids"] = IdArrayProp("Ids of elements to show") },
                new[] { "element_ids" }),

            // --- write ---
            Tool("set_parameter",
                "Set a parameter value on an element (or its type if the instance doesn't have it). Length parameters take mm, angles take degrees.",
                new() {
                    ["element_id"] = Prop("number", "The element id"),
                    ["parameter_name"] = Prop("string", "Exact parameter name as shown in Revit, e.g. 'Comments', 'Unconnected Height', 'Mark'"),
                    ["value"] = new JsonObject { ["description"] = "New value: string, number (mm for lengths, degrees for angles), or element id" }
                }, new[] { "element_id", "parameter_name", "value" }),
            Tool("set_element_type",
                "Change an element to a different type (e.g. swap a wall to another wall type, a door to another size).",
                new() {
                    ["element_id"] = Prop("number", "The element id"),
                    ["type_id"] = Prop("number", "Target type id (see list_element_types)")
                }, new[] { "element_id", "type_id" }),
            Tool("create_wall",
                $"Create a straight wall between two points on a level. {mm}",
                new() {
                    ["start"] = PointProp("Start point [x, y] in mm"),
                    ["end"] = PointProp("End point [x, y] in mm"),
                    ["level"] = Prop("string", "Level name or id (see list_levels)"),
                    ["height_mm"] = Prop("number", "Wall height in mm (default 3000)"),
                    ["type_id"] = Prop("number", "Optional wall type id (see list_element_types); default wall type used otherwise")
                }, new[] { "start", "end", "level" }),
            Tool("create_floor",
                $"Create a floor from a closed boundary of points on a level. {mm}",
                new() {
                    ["points"] = new JsonObject {
                        ["type"] = "array",
                        ["items"] = PointProp("[x, y] in mm"),
                        ["description"] = "Boundary points [[x,y],...] in mm, in order; the loop closes automatically"
                    },
                    ["level"] = Prop("string", "Level name or id"),
                    ["type_id"] = Prop("number", "Optional floor type id")
                }, new[] { "points", "level" }),
            Tool("create_level",
                "Create a new level at an elevation in mm, optionally named.",
                new() {
                    ["elevation_mm"] = Prop("number", "Elevation in mm"),
                    ["name"] = Prop("string", "Optional level name")
                }, new[] { "elevation_mm" }),
            Tool("create_grid",
                $"Create a straight grid line between two points. {mm}",
                new() {
                    ["start"] = PointProp("Start point [x, y] in mm"),
                    ["end"] = PointProp("End point [x, y] in mm"),
                    ["name"] = Prop("string", "Optional grid name, e.g. 'A' or '1'")
                }, new[] { "start", "end" }),
            Tool("create_text_note",
                $"Place a text note in a view (default: active view). {mm}",
                new() {
                    ["text"] = Prop("string", "The note text"),
                    ["position"] = PointProp("Position [x, y] in mm in the view"),
                    ["view_id"] = Prop("number", "Optional view id (default: active view)")
                }, new[] { "text", "position" }),
            Tool("place_family_instance",
                $"Place a loadable family instance (door, window, furniture, column...). Doors/windows need a host wall via host_id. {mm}",
                new() {
                    ["type_id"] = Prop("number", "Family type id (see list_element_types)"),
                    ["location"] = PointProp("Placement point [x, y, z] in mm"),
                    ["level"] = Prop("string", "Optional level name or id"),
                    ["host_id"] = Prop("number", "Optional host element id (e.g. the wall for a door/window)")
                }, new[] { "type_id", "location" }),
            Tool("move_elements",
                $"Move elements by a translation vector. {mm}",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of elements to move"),
                    ["vector_mm"] = PointProp("Translation [x, y] or [x, y, z] in mm")
                }, new[] { "element_ids", "vector_mm" }),
            Tool("copy_elements",
                $"Copy elements with a translation offset; returns the new element ids. {mm}",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of elements to copy"),
                    ["vector_mm"] = PointProp("Offset [x, y] or [x, y, z] in mm for the copies")
                }, new[] { "element_ids", "vector_mm" }),
            Tool("rotate_elements",
                "Rotate elements around a vertical axis through a center point. Angle in degrees, counter-clockwise in plan.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of elements to rotate"),
                    ["center"] = PointProp("Center point [x, y] in mm"),
                    ["angle_deg"] = Prop("number", "Rotation angle in degrees (counter-clockwise)")
                }, new[] { "element_ids", "center", "angle_deg" }),
            Tool("delete_elements",
                "Delete elements from the model by id. Dependent elements (e.g. doors hosted on a deleted wall) are deleted too. Confirm with the user before deleting many elements.",
                new() { ["element_ids"] = IdArrayProp("Ids of elements to delete") },
                new[] { "element_ids" }),

            // --- query & analysis (v2) ---
            Tool("list_sheets",
                "List all drawing sheets: id, number, name, and how many views are placed on each."),
            Tool("get_schedule_data",
                "Without arguments: lists all schedules. With view_id: returns that schedule's rows (cell text).",
                new() {
                    ["view_id"] = Prop("number", "Schedule view id (omit to list available schedules)"),
                    ["limit"] = Prop("number", "Max rows (default 200)")
                }),
            Tool("filter_elements",
                "Find elements of a category matching a condition — either a parameter test, or 'type_id' for " +
                "EVERY instance of one specific type (get an example element's type_id from get_element first), " +
                "or both together. Returns both full records ('elements') and a flat 'element_ids' array ready " +
                "to pass straight into another tool's element_ids argument. Lengths compare in mm, angles in degrees.",
                new() {
                    ["category"] = Prop("string", "Category name, e.g. 'Walls'"),
                    ["type_id"] = Prop("number", "Match every instance whose type is this (see get_element's 'type_id'). Can be used alone, or combined with parameter_name/value."),
                    ["parameter_name"] = Prop("string", "Parameter to test, e.g. 'Unconnected Height', 'Comments', 'Mark'"),
                    ["operator"] = Prop("string", "equals | not_equals | contains | greater | less (default equals)"),
                    ["value"] = Prop("string", "Value to compare against (number or text). Required together with parameter_name."),
                    ["limit"] = Prop("number", "Max matches (default 300)")
                }, new[] { "category" }),
            Tool("quantities_by_type",
                "Material takeoff style summary for a category: per type count, area m2, volume m3, length m.",
                new() { ["category"] = Prop("string", "Category name, e.g. 'Walls', 'Floors', 'StructuralColumns'") },
                new[] { "category" }),
            Tool("detect_clashes",
                "Approximate clash detection between two categories using bounding-box intersection (e.g. StructuralColumns vs Ducts).",
                new() {
                    ["category_a"] = Prop("string", "First category, e.g. 'StructuralColumns'"),
                    ["category_b"] = Prop("string", "Second category, e.g. 'Ducts', 'Pipes', 'Walls'"),
                    ["limit"] = Prop("number", "Max clash pairs (default 50)")
                }, new[] { "category_a", "category_b" }),

            // --- views & sheets (v2) ---
            Tool("set_active_view",
                "Switch Revit to a different view.",
                new() { ["view_id"] = Prop("number", "View id (see list_views)") },
                new[] { "view_id" }),
            Tool("create_floor_plan",
                "Create a floor plan view for a level. For MANY plans, use one batch call instead of repeating this.",
                new() {
                    ["level"] = Prop("string", "Level name or id"),
                    ["name"] = Prop("string", "Optional view name")
                }, new[] { "level" }),
            Tool("create_3d_view",
                "Create a new isometric 3D view.",
                new() { ["name"] = Prop("string", "Optional view name") }),
            Tool("duplicate_view",
                "Duplicate a view (default: the active view).",
                new() {
                    ["view_id"] = Prop("number", "View to duplicate (default active view)"),
                    ["mode"] = Prop("string", "with_detailing | plain | as_dependent (default with_detailing)"),
                    ["name"] = Prop("string", "Optional name for the copy")
                }),
            Tool("create_view_template",
                "Create a view template from an existing view (default: the active view) and name it. " +
                "The template captures the source view's display settings (visibility, scale, detail level, graphics).",
                new() {
                    ["name"] = Prop("string", "Name for the new view template"),
                    ["source_view_id"] = Prop("number", "View to build the template from (default active view; must be a plan/section/elevation/3D view)")
                }, new[] { "name" }),
            Tool("create_view_filter",
                "Create a named rule-based view filter (ParameterFilterElement), apply it to a view OR view template, " +
                "and hide (default) or show the matching elements. Example: hide all Walls whose 'Type Name' contains 'GLAZED' in template 'T1'.",
                new() {
                    ["name"] = Prop("string", "Name for the new filter"),
                    ["view_name"] = Prop("string", "Target view or VIEW TEMPLATE by name (default: active view; or use view_id)"),
                    ["view_id"] = Prop("number", "Target view id (alternative to view_name)"),
                    ["categories"] = new JsonObject {
                        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Categories the filter applies to, e.g. [\"Walls\"] (default Walls). Note: curtain walls are part of Walls."
                    },
                    ["parameter"] = Prop("string", "Parameter the rule tests, e.g. 'Type Name', 'Comments', 'Family Name'"),
                    ["operator"] = Prop("string", "equals | not_equals | contains | not_contains | begins_with | ends_with | greater | less"),
                    ["value"] = Prop("string", "Value to compare against"),
                    ["hide"] = new JsonObject { ["type"] = "boolean", ["description"] = "true (default) hides matching elements; false shows only the overrides" }
                }, new[] { "name", "parameter", "operator", "value" }),
            Tool("apply_view_template",
                "Apply an existing view template to one or more views (or remove it). " +
                "Different from create_view_template (which makes a new template from a view).",
                new() {
                    ["template_name"] = Prop("string", "Name of the template to apply (exact, case-insensitive)"),
                    ["template_id"] = Prop("number", "Template id (alternative to template_name)"),
                    ["view_ids"] = IdArrayProp("Target view ids (default: the active view; or use view_name)"),
                    ["view_name"] = Prop("string", "Single target view by name"),
                    ["remove"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = detach the template from the target views instead" }
                }),
            Tool("get_view_elements",
                "List the elements VISIBLE in a view (default: the active view) — total, counts per category, " +
                "and up to 'limit' element ids/names. Use this to see what a view actually shows.",
                new() {
                    ["view_name"] = Prop("string", "View by name (default: active view)"),
                    ["view_id"] = Prop("number", "View by id"),
                    ["category"] = Prop("string", "Optional filter, e.g. Walls, Doors"),
                    ["limit"] = Prop("number", "Max elements to return in detail (default 100)")
                }),
            Tool("create_schedule",
                "Create a schedule view for a category with the given column fields (e.g. a door schedule). " +
                "If a field name is wrong the error lists the valid field names.",
                new() {
                    ["category"] = Prop("string", "Category to schedule, e.g. Doors, Walls, Rooms, Windows"),
                    ["name"] = Prop("string", "Name for the schedule"),
                    ["fields"] = new JsonObject {
                        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Column field names, e.g. [\"Family and Type\", \"Level\", \"Count\"] (defaults chosen when omitted)"
                    },
                    ["sort_by"] = Prop("string", "Optional field name to sort ascending by")
                }, new[] { "category" }),
            Tool("create_section",
                "Create a SECTION view cutting along a line from 'start' to 'end' (plan coordinates, mm). " +
                "Looks perpendicular to that line. " + mm,
                new() {
                    ["start"] = PointProp("Cut line start [x, y] in mm"),
                    ["end"] = PointProp("Cut line end [x, y] in mm"),
                    ["bottom_mm"] = Prop("number", "Bottom of the view range in mm (default 0)"),
                    ["top_mm"] = Prop("number", "Top of the view range in mm (default 4000)"),
                    ["depth_mm"] = Prop("number", "How far the section looks in mm (default 3000)"),
                    ["name"] = Prop("string", "Optional name for the section view")
                }, new[] { "start", "end" }),
            Tool("create_dimension",
                "Create a linear dimension between 2+ elements in a view. Works best between GRIDS " +
                "(also columns); offset_mm moves the dimension line sideways.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of the elements to dimension between (2 or more, in order)"),
                    ["view_name"] = Prop("string", "Target view by name (default: active view)"),
                    ["view_id"] = Prop("number", "Target view by id"),
                    ["offset_mm"] = Prop("number", "Sideways offset of the dimension line in mm (default 1000)")
                }, new[] { "element_ids" }),
            Tool("create_wall_dimension",
                "Create a dimension between TWO WALLS using their real face references (via ReferenceIntersector), " +
                "not centerlines/midpoints — this is what create_dimension can't do for walls. Best for a simple " +
                "clear-width gap between two roughly parallel walls. Fails with a clear message if no clean face pair is found.",
                new() {
                    ["wall_a_id"] = Prop("number", "First wall's element id"),
                    ["wall_b_id"] = Prop("number", "Second wall's element id"),
                    ["view_id"] = Prop("number", "Target view to place the dimension in (default: active view; must not be a 3D view)"),
                    ["probe_point_mm"] = PointProp("Optional [x,y] point along wall A to probe from (default: wall A's midpoint)"),
                    ["offset_mm"] = Prop("number", "Sideways offset of the dimension line in mm (default 0)")
                }, new[] { "wall_a_id", "wall_b_id" }),
            Tool("create_revision",
                "Create a revision (sequence entry) and optionally add it to sheets' revision schedules.",
                new() {
                    ["description"] = Prop("string", "Revision description, e.g. 'Issued for construction'"),
                    ["date"] = Prop("string", "Revision date text, e.g. '2026-07-27'"),
                    ["issued_by"] = Prop("string", "Issued-by initials/name"),
                    ["sheet_ids"] = IdArrayProp("Optional sheet ids to add this revision to (list_sheets for ids)")
                }, new[] { "description" }),
            Tool("batch_rename",
                "Bulk-rename views, sheets, levels, grids or rooms: find/replace text in names, or add a " +
                "prefix/suffix. 'only_containing' limits which elements are touched.",
                new() {
                    ["kind"] = Prop("string", "views | sheets | levels | grids | rooms (default views)"),
                    ["find"] = Prop("string", "Text to find in the name (case-insensitive)"),
                    ["replace"] = Prop("string", "Replacement for 'find' (empty deletes it)"),
                    ["prefix"] = Prop("string", "Text to add at the start of every name"),
                    ["suffix"] = Prop("string", "Text to add at the end of every name"),
                    ["only_containing"] = Prop("string", "Only rename elements whose name contains this")
                }),
            Tool("create_elevation",
                "Place an elevation marker at a point in a floor plan and create one elevation view from it. " + mm,
                new() {
                    ["location"] = PointProp("Marker position [x, y] in mm"),
                    ["view_name"] = Prop("string", "The floor plan to place the marker in (default: active view, must be a plan)"),
                    ["view_id"] = Prop("number", "Plan view id (alternative to view_name)"),
                    ["direction_index"] = Prop("number", "Which face of the marker: 0..3 (default 0)"),
                    ["name"] = Prop("string", "Optional name for the elevation view")
                }, new[] { "location" }),
            Tool("measure_between_elements",
                "Measure between two elements: center-to-center distance, per-axis deltas, and the clear gap " +
                "between their bounding boxes. All results in mm.",
                new() {
                    ["element_ids"] = IdArrayProp("Exactly 2 element ids")
                }, new[] { "element_ids" }),
            Tool("create_sheet",
                "Create a drawing sheet, using the first title block unless title_block_type_id is given. For MANY sheets, use one batch call.",
                new() {
                    ["number"] = Prop("string", "Sheet number, e.g. 'A-101'"),
                    ["name"] = Prop("string", "Sheet name"),
                    ["title_block_type_id"] = Prop("number", "Optional title block type id (list_element_types with category TitleBlocks)")
                }),
            Tool("place_view_on_sheet",
                "Place a view on a sheet as a viewport. Location is in mm on the sheet (origin bottom-left). For MANY placements, use one batch call.",
                new() {
                    ["view_id"] = Prop("number", "The view to place"),
                    ["sheet_id"] = Prop("number", "The target sheet (see list_sheets)"),
                    ["location"] = PointProp("Optional center [x, y] on the sheet in mm")
                }, new[] { "view_id", "sheet_id" }),

            // --- view graphics (v2) ---
            Tool("hide_elements",
                "Permanently hide elements in a view (default: active view). Undoable.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids to hide"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "element_ids" }),
            Tool("unhide_elements",
                "Unhide previously hidden elements in a view.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids to unhide"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "element_ids" }),
            Tool("isolate_elements",
                "Temporarily isolate elements in a view (everything else hidden until reset_isolate).",
                new() {
                    ["element_ids"] = IdArrayProp("Ids to isolate"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "element_ids" }),
            Tool("reset_isolate",
                "Clear temporary hide/isolate in a view (default: active view).",
                new() { ["view_id"] = Prop("number", "Optional view id") }),
            Tool("override_element_color",
                "Color elements in a view (lines + solid surface fill), with optional transparency/halftone. Pass reset=true to clear overrides.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids to color"),
                    ["color"] = PointProp("[r, g, b] 0-255"),
                    ["transparency"] = Prop("number", "0-100 surface transparency"),
                    ["halftone"] = new JsonObject { ["type"] = "boolean", ["description"] = "Draw halftone" },
                    ["reset"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = remove overrides instead" },
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "element_ids" }),
            Tool("color_by_parameter",
                "Color-code ALL elements of a category by their parameter value, with automatic colours " +
                "(like pyRevit's Color Splash). Returns the value->color legend. This colours everything and " +
                "picks the colours itself — to colour only the elements matching a condition, in a colour you " +
                "choose, use color_elements_where instead.",
                new() {
                    ["category"] = Prop("string", "Category, e.g. 'Walls', 'Doors', 'Rooms'"),
                    ["parameter_name"] = Prop("string", "Parameter to group by, e.g. 'Type Name' or 'Mark'"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "category", "parameter_name" }),
            // --- routines: saved, reusable capabilities that become real ribbon buttons ---
            Tool("get_authoring_guide",
                "Read the AICon routine authoring guide (Markdown). Read this BEFORE writing or saving a " +
                "routine — it documents routine.json, both routine kinds, the input types, and the argument " +
                "placeholders. Returns length_chars first so you can judge the size.",
                new() { }),
            Tool("list_routines",
                "List the routines installed on this machine, with their ids, inputs and whether they are " +
                "read-only or destructive. Use this to find something that already exists before building it again.",
                new() { }),
            Tool("run_routine",
                "Run a saved routine by id. The whole routine is ONE undoable Revit transaction.",
                new() {
                    ["id"] = Prop("string", "Routine id from list_routines"),
                    ["inputs"] = new JsonObject { ["type"] = "object", ["description"] = "Values for the routine's declared inputs, keyed by input name" }
                }, new[] { "id" }),
            Tool("save_routine",
                "Save a capability as a permanent routine (it becomes a button in Revit). Pass the complete " +
                "routine.json as a string; for a script routine also pass the .cs sources in 'files'. Saving the " +
                "same id twice UPDATES it in place. Script routines are COMPILED FIRST — if compilation fails " +
                "nothing is saved and you get the errors with line numbers matching the source you sent. " +
                "Call get_authoring_guide first if you have not already.",
                new() {
                    ["routine_json"] = Prop("string", "The whole routine.json document as a JSON string"),
                    ["files"] = new JsonObject { ["type"] = "object", ["description"] = "Optional map of filename -> file content, e.g. {\"Routine.cs\": \"...\"}. C# NEVER goes inside routine_json." },
                    ["target_root"] = Prop("string", "Optional folder to save into (e.g. a team share). Defaults to this user's routines folder.")
                }, new[] { "routine_json" }),
            Tool("color_elements_where",
                "Colour ONLY the elements matching a condition, in a colour you choose — e.g. 'colour every " +
                "wall taller than 40000 in yellow' = {\"category\":\"Walls\",\"parameter_name\":\"Unconnected Height\"," +
                "\"operator\":\"greater\",\"value\":\"40000\",\"color\":\"yellow\"}. One call does the filtering and " +
                "the colouring. Lengths compare in MILLIMETRES.",
                new() {
                    ["category"] = Prop("string", "Category, e.g. 'Walls', 'Doors', 'Floors'"),
                    ["parameter_name"] = Prop("string", "Parameter to test, e.g. 'Unconnected Height', 'Area', 'Mark'"),
                    ["operator"] = Prop("string", "greater | less | equals | not_equals | contains (default greater)"),
                    ["value"] = Prop("string", "Value to compare against (number in mm, or text)"),
                    ["color"] = Prop("string", "Colour name (yellow, red, green, blue, orange, purple, cyan, magenta, brown, grey, black, white) or [r,g,b] 0-255"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "category", "parameter_name", "value", "color" }),
            Tool("tag_elements",
                "Tag elements by category in a view (a matching tag family must be loaded).",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of elements to tag"),
                    ["view_id"] = Prop("number", "Optional view id (default active)")
                }, new[] { "element_ids" }),

            // --- create (v2) ---
            Tool("create_room",
                $"Create a room at a point on a level (the point should be inside enclosing walls). {mm}",
                new() {
                    ["location"] = PointProp("Point [x, y] in mm inside the room boundary"),
                    ["level"] = Prop("string", "Level name or id"),
                    ["name"] = Prop("string", "Optional room name"),
                    ["number"] = Prop("string", "Optional room number")
                }, new[] { "location", "level" }),
            Tool("create_ceiling",
                $"Create a ceiling from a closed boundary at a height above a level. {mm}",
                new() {
                    ["points"] = new JsonObject {
                        ["type"] = "array", ["items"] = PointProp("[x, y] in mm"),
                        ["description"] = "Boundary points [[x,y],...] in mm"
                    },
                    ["level"] = Prop("string", "Level name or id"),
                    ["height_offset_mm"] = Prop("number", "Height above level (default 2800)"),
                    ["type_id"] = Prop("number", "Optional ceiling type id")
                }, new[] { "points", "level" }),
            Tool("create_column",
                $"Place a column at a point on a level. Uses the first loaded column type unless type_id is given. {mm}",
                new() {
                    ["location"] = PointProp("Point [x, y] in mm"),
                    ["level"] = Prop("string", "Base level name or id"),
                    ["height_mm"] = Prop("number", "Column height (default 3000)"),
                    ["type_id"] = Prop("number", "Optional column type id")
                }, new[] { "location", "level" }),
            Tool("create_beam",
                $"Create a structural beam between two points at a level (or explicit elevation). {mm}",
                new() {
                    ["start"] = PointProp("Start [x, y] in mm"),
                    ["end"] = PointProp("End [x, y] in mm"),
                    ["level"] = Prop("string", "Reference level name or id"),
                    ["elevation_mm"] = Prop("number", "Optional beam elevation (default: level elevation)"),
                    ["type_id"] = Prop("number", "Optional framing type id")
                }, new[] { "start", "end", "level" }),
            Tool("create_opening",
                $"Cut an opening in a host. Walls: pass corner1+corner2 ([x,y,z] mm). Floors/ceilings/roofs: pass points ([[x,y],...] mm). {mm}",
                new() {
                    ["host_id"] = Prop("number", "The wall/floor/ceiling/roof element id"),
                    ["corner1"] = PointProp("Wall opening corner 1 [x, y, z] in mm"),
                    ["corner2"] = PointProp("Wall opening corner 2 [x, y, z] in mm"),
                    ["points"] = new JsonObject {
                        ["type"] = "array", ["items"] = PointProp("[x, y] in mm"),
                        ["description"] = "Opening boundary for floor/ceiling/roof openings"
                    }
                }, new[] { "host_id" }),

            // --- modify (v2) ---
            Tool("mirror_elements",
                $"Mirror elements across a vertical plane defined by a line in plan. By default creates mirrored copies; set move=true to mirror the originals. {mm}",
                new() {
                    ["element_ids"] = IdArrayProp("Ids to mirror"),
                    ["axis_start"] = PointProp("Mirror axis start [x, y] in mm"),
                    ["axis_end"] = PointProp("Mirror axis end [x, y] in mm"),
                    ["move"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = move instead of copy" }
                }, new[] { "element_ids", "axis_start", "axis_end" }),
            Tool("set_parameter_bulk",
                "Set the same parameter to the same value on many elements at once (e.g. set 'Comments' on 50 walls).",
                new() {
                    ["element_ids"] = IdArrayProp("Target element ids"),
                    ["parameter_name"] = Prop("string", "Parameter name"),
                    ["value"] = new JsonObject { ["description"] = "Value (string, number in mm/degrees, or element id)" }
                }, new[] { "element_ids", "parameter_name", "value" }),
            Tool("set_workset",
                "Move element INSTANCES to a workset by name (only works on a workshared model). To move every " +
                "instance of one type, first get_element on one example to read its type_id, then filter_elements " +
                "with that type_id to get element_ids, then pass those here. Note: a TYPE itself cannot be moved " +
                "to a workset — Revit assigns types to a fixed system workset by category — only instances can.",
                new() {
                    ["element_ids"] = IdArrayProp("Ids of the element instances to move"),
                    ["workset_name"] = Prop("string", "Exact workset name (case-insensitive) to move them to")
                }, new[] { "element_ids", "workset_name" }),
            Tool("rename_element",
                "Rename an element (views, levels, sheets, types, materials...).",
                new() {
                    ["element_id"] = Prop("number", "Element id"),
                    ["name"] = Prop("string", "New name")
                }, new[] { "element_id", "name" }),
            Tool("join_geometry",
                "Join or unjoin the geometry of two overlapping elements (e.g. wall and floor).",
                new() {
                    ["element_id_a"] = Prop("number", "First element"),
                    ["element_id_b"] = Prop("number", "Second element"),
                    ["action"] = Prop("string", "join | unjoin (default join)")
                }, new[] { "element_id_a", "element_id_b" }),

            // --- export & file (v2) ---
            Tool("export_pdf",
                "Export views to a combined PDF in the 'AICon Exports' folder on the Desktop. Default: the active view.",
                new() {
                    ["view_ids"] = IdArrayProp("Optional view ids (default: active view)"),
                    ["file_name"] = Prop("string", "Optional file name without extension")
                }),
            Tool("export_ifc",
                "Export the model to IFC in the 'AICon Exports' folder on the Desktop.",
                new() { ["file_name"] = Prop("string", "Optional file name without extension") }),
            Tool("save_document",
                "Save the current Revit document to disk."),
            Tool("load_family",
                "Load a family (.rfa) file into the project and list its types.",
                new() { ["path"] = Prop("string", "Full path to the .rfa file") },
                new[] { "path" }),

            // --- the escape hatch ---
            Tool("run_code",
                "Execute C# code inside Revit for ANYTHING not covered by other tools. The code is the BODY of: object Run(UIApplication app, UIDocument uidoc, Document doc) — it must end with a return statement. " +
                "Runs inside a transaction automatically (set no_transaction=true for read-only code). " +
                "Compiler is Roslyn (modern C#) — string interpolation ($\"...\"), ?., 'var', pattern matching, LINQ all work normally. " +
                "Available namespaces: Autodesk.Revit.DB (+Architecture/Structure/Mechanical/Plumbing/Electrical), Autodesk.Revit.UI, System, System.Linq, System.Collections.Generic. " +
                "Revit internal units are FEET — convert mm/304.8. Return a string/number/Dictionary<string,object>/List<object> describing the result. " +
                "REQUIRES CONFIRMATION: a call without \"confirmed\": true does not compile or execute anything — it only echoes the code back " +
                "so it can be reviewed first. Call again with the SAME arguments plus \"confirmed\": true to actually run it.",
                new() {
                    ["code"] = Prop("string", "C# statements; must return object"),
                    ["no_transaction"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = don't wrap in a transaction (read-only code)" },
                    ["confirmed"] = new JsonObject { ["type"] = "boolean", ["description"] = "Must be true to actually compile and run. Omit (or false) on the first call to review the code first; the response then echoes it back unexecuted." }
                }, new[] { "code" })
        );
    }

    // Curated subset offered to SMALL LOCAL models (7–8B via Ollama). Two reasons:
    //  1. 76 schemas ≈ thousands of prompt tokens — small models drown and pick badly.
    //  2. run_code/batch need skills (valid Revit C#, composing nested ops) small models
    //     demonstrably lack — given the chance they emit broken code instead of using the
    //     perfectly good dedicated tool. Removing the trap forces the right choice.
    // Cloud models (Gemini/DeepSeek/…) keep the full catalogue.
    private static readonly HashSet<string> LocalToolNames = new()
    {
        // read / query
        "get_project_info", "list_levels", "list_views", "list_sheets", "list_rooms",
        "list_categories", "list_elements", "list_element_types", "get_element",
        "get_selection", "filter_elements", "quantities_by_type", "get_schedule_data",
        "measure_between_elements",
        // see & point things out
        "export_view_image", "select_elements", "show_elements", "color_by_parameter", "color_elements_where",
        // routines: a small model can usefully LIST and RUN a saved routine (one call, no authoring)
        "list_routines", "run_routine",
        // create
        "create_sheet", "create_level", "create_grid", "create_wall", "create_floor",
        "create_floor_plan", "create_3d_view", "create_view_template", "create_view_filter",
        "apply_view_template", "get_view_elements", "create_schedule", "create_section",
        "create_revision", "batch_rename", "create_elevation",
        "create_text_note", "place_view_on_sheet", "create_room",
        // modify
        // (save_document deliberately NOT offered locally: small models compulsively call it after
        //  every action regardless of prompt rules; the user can save from Revit's own UI.)
        "set_parameter", "rename_element", "delete_elements", "set_active_view"
    };

    // The reduced tool list for local models. Filters the single source of truth, so schemas
    // can never drift from the full catalogue.
    public static JsonArray BuildLocalToolList()
    {
        var arr = new JsonArray();
        foreach (JsonNode? t in BuildToolList())
        {
            string? name = t?["name"]?.GetValue<string>();
            if (name != null && LocalToolNames.Contains(name))
                arr.Add(t!.DeepClone());
        }
        return arr;
    }
}
