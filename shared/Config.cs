#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICon.Agent
{
    // Runtime configuration. One file, one line to change to swap the whole AI backend:
    //   local  : { "provider":"openai", "baseUrl":"http://localhost:11434/v1", "model":"qwen2.5-coder:7b" }
    //   DeepSeek: { "provider":"openai", "baseUrl":"https://api.deepseek.com", "model":"deepseek-chat", "apiKeyEnv":"DEEPSEEK_API_KEY" }
    //   ChatGPT : { "provider":"openai", "baseUrl":"https://api.openai.com/v1", "model":"gpt-4o", "apiKeyEnv":"OPENAI_API_KEY" }
    //   Gemini  : { "provider":"gemini", "model":"gemini-flash-latest", "apiKey":"AQ...." }
    //
    // For the in-Revit chat panel the user can also keep SEVERAL agents ready and switch between them
    // from the ribbon (Gemini / DeepSeek / Local). That uses the optional "profiles" map plus
    // "activeProfile":
    //   {
    //     "activeProfile": "gemini",
    //     "profiles": {
    //       "gemini":   { "model": "gemini-3.6-flash", "apiKey": "AQ..." },
    //       "deepseek": { "apiKey": "sk-..." },
    //       "local":    { "model": "qwen2.5-coder:7b" }
    //     }
    //   }
    // Anything a profile omits falls back to a sensible built-in default for that agent, so a profile
    // usually only needs to carry its API key (or a model override).
    public sealed class Config
    {
        // Well-known profile keys — the three agents the ribbon exposes as buttons.
        public const string ProfileGemini = "gemini";
        public const string ProfileDeepSeek = "deepseek";
        public const string ProfileLocal = "local";

        // Which IProvider to use. "openai" (OpenAI-compatible: local Ollama/LM Studio, DeepSeek,
        // ChatGPT) or "gemini". Future: "anthropic".
        [JsonPropertyName("provider")] public string Provider { get; set; } = "openai";

        // Base URL of the chat-completions endpoint. Local models need no key.
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://localhost:11434/v1";

        [JsonPropertyName("model")] public string Model { get; set; } = "qwen2.5-coder:7b";

        // Secrets: prefer apiKeyEnv (name of an environment variable holding the key). apiKey is a
        // fallback for convenience / local setups and for the in-Revit panel (Revit does not inherit
        // a terminal's session environment variables).
        [JsonPropertyName("apiKeyEnv")] public string? ApiKeyEnv { get; set; }
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }

        // Multi-agent (ribbon) support. Which profile is active, and the per-agent overrides. Both are
        // optional; when absent the flat fields above behave exactly as before.
        [JsonPropertyName("activeProfile")] public string? ActiveProfile { get; set; }
        [JsonPropertyName("profiles")] public Dictionary<string, ProfileConfig>? Profiles { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Resolve the API key: explicit apiKey wins, else the named environment variable, else null
        // (fine for local models that don't authenticate).
        public string? ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey)) return ApiKey;
            if (!string.IsNullOrWhiteSpace(ApiKeyEnv))
            {
                string? v = Environment.GetEnvironmentVariable(ApiKeyEnv!);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        // Produce the effective flat Config for a given agent/profile key. Resolution order:
        //   1. no key requested (and no activeProfile set) → this flat config, unchanged (legacy).
        //   2. built-in default for the agent  →  overlaid by profiles[key] from the file (file wins).
        //   3. if the result still has no API key but the flat config is for the same provider family,
        //      inherit the flat config's key — so an existing single-provider setup keeps working when
        //      its matching ribbon button is pressed.
        public Config ForProfile(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) key = ActiveProfile;
            if (string.IsNullOrWhiteSpace(key)) return this;   // pure flat / legacy behaviour

            Config def = DefaultProfile(key!);
            if (Profiles != null && Profiles.TryGetValue(key!, out ProfileConfig? p) && p != null)
                def = Overlay(def, p);

            // If the resolved profile has no usable key yet, inherit the flat config's key when it's for
            // the same provider family — so an existing single-provider file keeps working. Uses the
            // RESOLVED key (not just the raw fields) so a placeholder's apiKeyEnv doesn't block this.
            if (string.IsNullOrWhiteSpace(def.ResolveApiKey()) && Family(Provider) == Family(def.Provider))
            {
                def.ApiKey = ApiKey;
                def.ApiKeyEnv = ApiKeyEnv;
            }
            return def;
        }

        // Whether the given agent typically requires an API key (cloud). Local endpoints do not.
        public static bool RequiresApiKey(Config cfg)
        {
            string prov = (cfg.Provider ?? "").ToLowerInvariant();
            if (prov == "gemini" || prov == "google") return true;
            string url = (cfg.BaseUrl ?? "").ToLowerInvariant();
            bool local = url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
            return !local;
        }

        // A friendly label for an agent/profile key (used in the chat panel).
        public static string Label(string? key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case ProfileGemini: return "Gemini";
                case ProfileDeepSeek: return "DeepSeek";
                case ProfileLocal: return "Local model";
                default: return string.IsNullOrWhiteSpace(key) ? "the configured agent" : key!;
            }
        }

        // Built-in defaults for each ribbon agent. A profile in the file only needs to override what
        // differs (usually just the API key).
        public static Config DefaultProfile(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case ProfileGemini:
                    // Blank baseUrl → GeminiProvider uses the public Generative Language API.
                    // Was "gemini-2.0-flash" until Google retired it (2026-08-23): a live call started
                    // returning 404 "This model models/gemini-2.0-flash is no longer available. Please
                    // update your code to use models/gemini-3.6-flash" — that message is Google's own,
                    // taken verbatim, not a guess. A user's own aiconagent.json profile can still
                    // override "model" if a newer one replaces this in turn.
                    return new Config { Provider = "gemini", BaseUrl = "", Model = "gemini-3.6-flash" };
                case ProfileDeepSeek:
                    return new Config
                    {
                        Provider = "openai",
                        BaseUrl = "https://api.deepseek.com",
                        Model = "deepseek-chat",
                        ApiKeyEnv = "DEEPSEEK_API_KEY"
                    };
                case ProfileLocal:
                    return new Config { Provider = "openai", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5-coder:7b" };
                default:
                    return new Config();
            }
        }

        // Set the active agent and remember it. Also seeds empty placeholders for the three known
        // agents (only the ones missing) so the user can discover where to paste each key. Never
        // overwrites values the user already set.
        public static void PersistActiveProfile(string? key)
        {
            string usedPath;
            Config cfg = Load(null, out usedPath);
            cfg.ActiveProfile = key;
            cfg.Profiles = cfg.Profiles ?? new Dictionary<string, ProfileConfig>();
            SeedPlaceholder(cfg, ProfileGemini);
            SeedPlaceholder(cfg, ProfileDeepSeek);
            SeedPlaceholder(cfg, ProfileLocal);
            cfg.Save(usedPath);
        }

        // Save an API key for an agent, entered through the in-Revit prompt. Writes it into that agent's
        // profile (creating the profile from its defaults if needed) and makes it the active agent, so
        // the key is never requested again. A null/blank profileKey stores to the flat apiKey instead.
        public static void SaveApiKey(string? profileKey, string apiKey)
        {
            string usedPath;
            Config cfg = Load(null, out usedPath);
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                cfg.ApiKey = apiKey;
            }
            else
            {
                cfg.Profiles = cfg.Profiles ?? new Dictionary<string, ProfileConfig>();
                if (!cfg.Profiles.TryGetValue(profileKey!, out ProfileConfig? p) || p == null)
                {
                    p = NewPlaceholder(profileKey!);
                    cfg.Profiles[profileKey!] = p;
                }
                p.ApiKey = apiKey;
                cfg.ActiveProfile = profileKey;
            }
            cfg.Save(usedPath);
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
        }

        private static void SeedPlaceholder(Config cfg, string key)
        {
            if (cfg.Profiles!.ContainsKey(key)) return;
            cfg.Profiles[key] = NewPlaceholder(key);
        }

        // A discoverable, key-less profile stub carrying just provider/baseUrl/model from the defaults.
        // No apiKeyEnv is written (the built-in default still applies at resolve time), so the file stays
        // clean and the user only needs to fill in "apiKey".
        private static ProfileConfig NewPlaceholder(string key)
        {
            Config d = DefaultProfile(key);
            return new ProfileConfig
            {
                Provider = d.Provider,
                BaseUrl = string.IsNullOrWhiteSpace(d.BaseUrl) ? null : d.BaseUrl,
                Model = d.Model
                // ApiKey / apiKeyEnv deliberately left null — the prompt (or the user) fills apiKey.
            };
        }

        private static Config Overlay(Config def, ProfileConfig p)
        {
            return new Config
            {
                // The profile KEY decides the provider; only honour an explicit provider override.
                Provider = string.IsNullOrWhiteSpace(p.Provider) ? def.Provider : p.Provider!,
                BaseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? def.BaseUrl : p.BaseUrl!,
                Model = string.IsNullOrWhiteSpace(p.Model) ? def.Model : p.Model!,
                ApiKey = string.IsNullOrWhiteSpace(p.ApiKey) ? def.ApiKey : p.ApiKey,
                ApiKeyEnv = string.IsNullOrWhiteSpace(p.ApiKeyEnv) ? def.ApiKeyEnv : p.ApiKeyEnv
            };
        }

        private static string Family(string? provider)
        {
            string p = (provider ?? "").ToLowerInvariant();
            return (p == "gemini" || p == "google") ? "gemini" : "openai";
        }

        // Search order: explicit path arg → aiconagent.json next to the exe/dll → %APPDATA%\AICon\aiconagent.json.
        // If none is found, writes a starter file next to the assembly and returns its defaults.
        public static Config Load(string? explicitPath, out string usedPath)
        {
            foreach (string candidate in CandidatePaths(explicitPath))
            {
                if (File.Exists(candidate))
                {
                    usedPath = candidate;
                    string json = File.ReadAllText(candidate);
                    Config? cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
                    if (cfg == null) throw new InvalidDataException("Could not parse " + candidate);
                    return cfg;
                }
            }

            // Nothing found: drop a template in %APPDATA%\AICon (always writable, and a searched
            // path — unlike the assembly folder, which inside Revit is the read-only install dir).
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon");
            Directory.CreateDirectory(appDataDir);
            string target = Path.Combine(appDataDir, "aiconagent.json");
            var starter = new Config();
            File.WriteAllText(target, JsonSerializer.Serialize(starter, JsonOpts), new UTF8Encoding(false));
            usedPath = target;
            return starter;
        }

        private static IEnumerable<string> CandidatePaths(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath!;
            yield return Path.Combine(AppContext.BaseDirectory, "aiconagent.json");
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "AICon", "aiconagent.json");
        }
    }

    // Per-agent overrides for the "profiles" map. All fields are optional (nullable, no defaults) so
    // that "absent" is distinguishable from "set to the default value" — an omitted field inherits the
    // built-in default for that agent (see Config.DefaultProfile).
    public sealed class ProfileConfig
    {
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("apiKeyEnv")] public string? ApiKeyEnv { get; set; }
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    }
}
