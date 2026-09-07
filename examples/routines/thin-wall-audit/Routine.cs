// Bare-body form: no class, no usings, no boilerplate.
// AICon wraps this automatically and puts these in scope for you:
//     uiapp   (UIApplication)   uidoc (UIDocument)   doc (Document)
//     input   — the routine's inputs, e.g. input.Number("maxThicknessMm"), input.Bool("includeLinks")
// Return any object; it is shown to the user and returned to the AI as the routine's result.
//
// Generics are completely fine here — this file is compiled from disk and never travels
// through the run_code transport.

double maxMm = input.Number("maxThicknessMm", 150);
bool includeLinks = input.Bool("includeLinks", false);

var byType = new Dictionary<string, List<int>>();
int scanned = 0;

// .OfType<Wall>(), not .Cast<Wall>(): OST_Walls can also contain in-place FamilyInstance
// elements (e.g. a curtain/solar-panel system authored under the Walls category), and a
// blind Cast<Wall>() throws on the first one it meets. OfType<Wall>() just skips them.
foreach (Wall w in new FilteredElementCollector(doc)
             .OfCategory(BuiltInCategory.OST_Walls)
             .WhereElementIsNotElementType()
             .OfType<Wall>())
{
    scanned++;
    double widthMm = w.Width * 304.8;          // Revit is in feet; AICon reports millimetres
    if (widthMm >= maxMm) continue;

    string typeName = w.Name;
    if (!byType.ContainsKey(typeName)) byType[typeName] = new List<int>();
    byType[typeName].Add(w.Id.IntegerValue);
}

// Linked models are a separate document each — only walk them when asked, they are slow.
int linkedHits = 0;
if (includeLinks)
{
    foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                 .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
    {
        Document ldoc = link.GetLinkDocument();
        if (ldoc == null) continue;             // link unloaded
        foreach (Wall w in new FilteredElementCollector(ldoc)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType().OfType<Wall>())
            if (w.Width * 304.8 < maxMm) linkedHits++;
    }
}

var report = new Dictionary<string, object>();
report["walls_scanned"] = scanned;
report["threshold_mm"] = maxMm;
report["types_below_threshold"] = byType.Count;
report["walls_below_threshold"] = byType.Values.Sum(v => v.Count);
report["by_type"] = byType.OrderByDescending(kv => kv.Value.Count)
    .Take(25)
    .Select(kv => (object)new Dictionary<string, object>
    {
        { "type", kv.Key },
        { "count", kv.Value.Count },
        { "example_ids", kv.Value.Take(5).ToList() }
    }).ToList();
if (includeLinks) report["walls_below_threshold_in_links"] = linkedHits;

return report;
