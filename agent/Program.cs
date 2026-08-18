using System.Text;
using System.Text.Json.Nodes;
using AICon.Agent;
using AICon.Shared;

// AICon Agent — a self-contained console host that lets ANY AI model (local or cloud) drive the
// live Revit session through the existing AICon bridge. The model is swappable behind IProvider;
// the conversation loop (Agent), the tool catalogue, and tool dispatch are shared with the in-Revit
// chat panel — this file is just the console front-end.
//
//   You  ─►  AIConAgent  ─►  IProvider (local/cloud model)
//                    └──────►  HttpToolExecutor  ─►  Revit bridge (localhost:55234)  ─►  AICon add-in

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

// --- config -------------------------------------------------------------------------------
string? explicitConfigPath = args.Length > 0 ? args[0] : null;
Config cfg;
string configPath;
try
{
    cfg = Config.Load(explicitConfigPath, out configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not load configuration: {ex.Message}");
    return 1;
}

// --- provider (the swappable seam) --------------------------------------------------------
IProvider provider;
try
{
    provider = ProviderFactory.Create(cfg);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

provider.Status = msg => Console.WriteLine($"    … {msg}");
var executor = new HttpToolExecutor();
// Small local models (Ollama on localhost) get the curated tool subset + short example-driven
// prompt (same policy as the in-Revit panel); cloud models get the full catalogue.
bool isLocalModel = cfg.BaseUrl != null &&
    (cfg.BaseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
     cfg.BaseUrl.Contains("127.0.0.1"));
JsonArray tools = isLocalModel ? Tools.BuildLocalToolList() : Tools.BuildToolList();
var agent = new Agent(provider, tools, executor,
    isLocalModel ? Agent.LocalSystemPrompt() : Agent.DefaultSystemPrompt());

// Wire the shared loop's events to console output.
agent.AssistantText += text => Console.WriteLine($"\nai  › {text}");
agent.ToolStarted += (name, argsPreview) => Console.WriteLine($"    ⚙ {name} {argsPreview}");
agent.ToolFinished += preview => Console.WriteLine($"    ↳ {preview}");
agent.Notice += note => Console.WriteLine($"\n[{note}]");

// --- banner ---------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("  ===========================================");
Console.WriteLine("     AICon Agent — Revit + your AI model");
Console.WriteLine("  ===========================================");
Console.WriteLine($"  Provider : {provider.Name}");
Console.WriteLine($"  Endpoint : {cfg.BaseUrl}");
Console.WriteLine($"  Config   : {configPath}");
Console.WriteLine($"  Tools    : {tools.Count} (talking to Revit on localhost:55234)");
Console.WriteLine();
Console.WriteLine("  Type a request in plain language. 'exit' to quit.");
Console.WriteLine("  e.g.  What's in my Revit model?");
Console.WriteLine("        Create a 4m x 3m room with 3m walls on Level 1.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (true)
{
    Console.Write("you › ");
    string? input = Console.ReadLine();
    if (input is null) break;                         // stdin closed
    input = input.Trim();
    if (input.Length == 0) continue;
    if (input is "exit" or "quit") break;

    try
    {
        await agent.SendAsync(input, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[cancelled]");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[error] {ex.Message}");
    }
    Console.WriteLine();
}

Console.WriteLine("bye.");
return 0;
