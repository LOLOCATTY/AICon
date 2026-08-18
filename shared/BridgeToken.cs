#nullable enable
using System;
using System.IO;

namespace AICon.Shared;

// Reads the shared-secret token the Revit add-in's localhost bridge (plugin/BridgeServer.cs) requires
// on every tool call. Read FRESH on every call rather than cached once at process startup: this client
// process (the MCP server, or the console agent) may start before Revit does, in which case the token
// file does not exist yet — re-reading lets a Revit that starts LATER be picked up without restarting
// this process. The token itself is generated and owned by the add-in, not by this file.
public static class BridgeToken
{
    public static string? Load()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "bridge.token");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }
}
