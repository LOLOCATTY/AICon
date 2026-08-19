using Autodesk.Revit.DB;

namespace AICon
{
    /// <summary>
    /// The ONE place that knows about the ElementId.IntegerValue / .Value split between Revit
    /// versions, so the other ~120 call sites across the plugin don't each need to know.
    ///
    /// Revit 2023/2024 (net48, this build's target today): ElementId.IntegerValue (int) and
    /// ElementId(int) are the only APIs; .Value/ElementId(long) don't exist yet.
    /// Revit 2024+ ALSO has ElementId.Value (long) and ElementId(long) — IntegerValue/ElementId(int)
    /// still work there but are marked obsolete (AICon.csproj suppresses CS0618 for it deliberately).
    /// Revit 2025+ (a future net8.0-windows target, not yet added — this machine has no Revit 2025 to
    /// build or verify against) REMOVES IntegerValue/ElementId(int) entirely — only .Value/ElementId(long)
    /// exist there.
    ///
    /// AICon's wire format (the JSON element ids every tool sends/receives) stays 'int' on purpose —
    /// switching the whole wire protocol to 'long' would ripple into every tool schema and every MCP
    /// client for no real benefit (a Revit ElementId in practice never approaches ~2 billion). This
    /// class is only about which Revit-side member to call; every caller keeps working with plain int.
    ///
    /// #if NETFRAMEWORK (not the more specific NET48) so this also covers a net481 or similar minor
    /// bump without edits. The #else branch targets Revit 2025+ but is UNVERIFIED — this machine has
    /// no Revit 2025 install to compile or test it against yet. Do not trust it until it has been
    /// built against a real Revit 2025 SDK.
    /// </summary>
    internal static class ElementIdCompat
    {
#if NETFRAMEWORK
        public static int ToInt(this ElementId id) => id.IntegerValue;

        public static ElementId FromInt(int value) => new ElementId(value);
#else
        // UNVERIFIED — written for Revit 2025+ but never compiled or run against it.
        public static int ToInt(this ElementId id) => (int)id.Value;

        public static ElementId FromInt(int value) => new ElementId((long)value);
#endif
    }
}
