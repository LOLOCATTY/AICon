using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICon
{
    // Configuration for AR400's optional "AI decision layer" — a handful of narrow, structured calls
    // (never the whole pipeline) to an LLM for genuinely ambiguous choices: which Excel column is
    // "Level"?, which tag position avoids an overlap?, which wall-face pair is the intended one to
    // dimension? Kept separate from aiconagent.json (the interactive CHAT agent's config) since these
    // are unattended, structured decisions, not a conversation — they shouldn't change just because
    // the user points the chat panel at a different model.
    //
    // NOTE: this machine has no Node.js, so unlike the original spec (a separate decision-service
    // process) these calls go straight from this plugin to OpenRouter/Anthropic over HTTPS — see
    // AIConDecisionClient.cs. Same idea (swappable provider, JSON-validated, fails safe), one less
    // process to keep running next to Revit.
    public sealed class AIConAiConfig
    {
        [JsonPropertyName("provider")] public string Provider { get; set; } = "openrouter";
        [JsonPropertyName("model")] public string Model { get; set; } = "qwen/qwen3-32b";
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
        // Optional endpoint override — for a corporate proxy / gateway, or a local test server.
        // Blank uses the provider's public endpoint.
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";

        // Used only when the primary call fails validation twice, or has no key configured at all —
        // a one-line swap to a stronger, still-cheap model.
        [JsonPropertyName("fallbackProvider")] public string FallbackProvider { get; set; } = "anthropic";
        [JsonPropertyName("fallbackModel")] public string FallbackModel { get; set; } = "claude-haiku-4-5-20251001";
        [JsonPropertyName("fallbackApiKey")] public string FallbackApiKey { get; set; } = "";
        [JsonPropertyName("fallbackBaseUrl")] public string FallbackBaseUrl { get; set; } = "";

        // Master switch — AR400 Settings exposes this as "Use AI-assisted decisions". On by default,
        // but harmless either way: with no API key set, every decision call is a no-op (returns null
        // immediately) regardless of this flag.
        [JsonPropertyName("useAiDecisions")] public bool UseAiDecisions { get; set; } = true;

        // Which of the chat panel's agents answers these background questions: "gemini", "deepseek",
        // "local", or blank for whichever agent the chat panel is currently set to. Useful because
        // these are small, rare, throwaway questions — worth pointing at a free tier (Gemini) or an
        // offline model (local/Ollama) even when the chat itself runs on something else.
        [JsonPropertyName("useProfile")] public string UseProfile { get; set; } = "";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "ar400ai.json");

        public static AIConAiConfig Load()
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                try
                {
                    AIConAiConfig cfg = JsonSerializer.Deserialize<AIConAiConfig>(File.ReadAllText(path), JsonOpts);
                    return cfg ?? new AIConAiConfig();
                }
                catch { return new AIConAiConfig(); }   // corrupt/hand-edited file — behave as "not configured"
            }

            // First run: drop a template with empty keys (so the file exists and is discoverable) and
            // a plain-text README next to it explaining each field (JSON itself can't hold comments).
            var starter = new AIConAiConfig();
            starter.Save();
            WriteReadme(Path.GetDirectoryName(path));
            return starter;
        }

        public void Save()
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
        }

        // [JsonIgnore] matters: without it System.Text.Json writes these computed values into
        // ar400ai.json as though they were editable settings, which is confusing and meaningless.
        [JsonIgnore] public bool HasPrimaryKey => !string.IsNullOrWhiteSpace(ApiKey);
        [JsonIgnore] public bool HasFallbackKey => !string.IsNullOrWhiteSpace(FallbackApiKey);

        private static void WriteReadme(string dir)
        {
            string readme = Path.Combine(dir, "ar400ai-README.txt");
            if (File.Exists(readme)) return;
            string text =
                "ar400ai.json — AR400's optional AI decision layer\n" +
                "===================================================\n\n" +
                "AR400 stays fully deterministic without this file filled in. It only asks an AI model at\n" +
                "a few genuinely ambiguous points (e.g. matching Excel column headers that don't match the\n" +
                "expected names). With no API key set below, AR400 behaves exactly as if this didn't exist.\n\n" +
                "provider / model / apiKey        Primary model. Default: OpenRouter, qwen/qwen3-32b.\n" +
                "                                  Get a key at https://openrouter.ai/keys and paste it as apiKey.\n\n" +
                "fallbackProvider / fallbackModel / fallbackApiKey\n" +
                "                                  Used only if the primary call fails twice in a row.\n" +
                "                                  Default: Anthropic Claude Haiku. Key from https://console.anthropic.com/\n\n" +
                "useAiDecisions                    Set to false to turn the whole layer off (same effect as\n" +
                "                                  leaving apiKey blank). Also toggleable from AR400 Settings.\n\n" +
                "Supported values for provider / fallbackProvider: \"openrouter\", \"anthropic\".\n";
            try { File.WriteAllText(readme, text, new UTF8Encoding(false)); } catch { }
        }
    }
}
