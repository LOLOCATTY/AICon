using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AICon
{
    // The "AI decision layer" for AR400 (and future commands): a handful of narrow, structured LLM
    // calls at genuinely ambiguous points — nothing else in AR400 depends on this class existing or
    // working. Every public method fails SAFE: any network error, timeout, or malformed/low-confidence
    // response returns null, and the caller must already have a deterministic fallback ready to use.
    //
    // Calls providers DIRECTLY over HTTPS — no separate service/process. This machine has no Node.js,
    // and AICon already proves out-of-process-free LLM calls work fine from inside Revit's net48 host
    // (see shared\OpenAiCompatibleProvider.cs / GeminiProvider.cs, used by the chat panel). OpenRouter
    // speaks the same /chat/completions shape as OpenAI; Anthropic's Messages API is a second small
    // adapter. Deliberately NOT reusing the heavier Agent/Provider chat-loop classes — this only ever
    // needs one system+user prompt in, one JSON object out, no conversation history, no tool-calling.
    internal static class AIConDecisionClient
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

        public sealed class StatusInfo
        {
            public bool Enabled;
            public bool Configured;
            public string Provider;
            public string Model;
        }

        /// <summary>No network call — just reports what would answer a decision, for the AR400
        /// Settings status line. Normally that's the same AI the chat panel uses.</summary>
        public static StatusInfo GetStatus()
        {
            AIConAiConfig cfg = AIConAiConfig.Load();
            if (cfg.HasPrimaryKey || cfg.HasFallbackKey)
                return new StatusInfo { Enabled = cfg.UseAiDecisions, Configured = true, Provider = cfg.Provider, Model = cfg.Model };

            // Nothing dedicated — report the chat panel's provider, which is what will actually run.
            try
            {
                string usedPath;
                Agent.Config agentCfg = Agent.Config.Load(null, out usedPath)
                    .ForProfile(string.IsNullOrWhiteSpace(cfg.UseProfile) ? null : cfg.UseProfile);
                bool usable = agentCfg != null &&
                              (!Agent.Config.RequiresApiKey(agentCfg) || !string.IsNullOrWhiteSpace(agentCfg.ResolveApiKey()));
                string source = string.IsNullOrWhiteSpace(cfg.UseProfile)
                    ? " (same as AI Chat)"
                    : " (" + Agent.Config.Label(cfg.UseProfile) + ")";
                return new StatusInfo
                {
                    Enabled = cfg.UseAiDecisions,
                    Configured = usable,
                    Provider = agentCfg != null ? agentCfg.Provider + source : "",
                    Model = agentCfg != null ? agentCfg.Model : ""
                };
            }
            catch
            {
                return new StatusInfo { Enabled = cfg.UseAiDecisions, Configured = false, Provider = "", Model = "" };
            }
        }

        // ---------------------------------------------------------------------------------------
        // 1) Excel column mapping
        // ---------------------------------------------------------------------------------------

        public sealed class ExcelColumnMapping
        {
            public string Package, Level, SheetNumber, SheetName;
            public double Confidence;
        }

        private const string ExcelColumnsSystemPrompt =
            "You map spreadsheet column headers to 4 fields for a Revit architectural sheet register: " +
            "\"package\" (the shop-drawing package/discipline, e.g. Blockwork), \"level\" (the building " +
            "level/floor), \"sheetNumber\" (the drawing number), \"sheetName\" (the drawing title). " +
            "You are given the real header row and a few sample data rows. Reply with ONLY this JSON " +
            "object, no prose, no markdown fences: " +
            "{\"mapping\":{\"package\":<header or null>,\"level\":<header or null>,\"sheetNumber\":<header or null>,\"sheetName\":<header or null>},\"confidence\":<0 to 1>}. " +
            "Each mapped value MUST be exactly one of the header strings you were given, or null if none fits — " +
            "never invent a header that was not given to you.";

        /// <summary>Asks which header maps to which of the 4 sheet-register fields. Returns null on any
        /// failure/timeout/invalid response — the caller must fall back to its own exact-header matching.</summary>
        public static ExcelColumnMapping DecideExcelColumns(List<string> headers, List<List<string>> sampleRows)
        {
            AIConAiConfig cfg = AIConAiConfig.Load();
            if (!cfg.UseAiDecisions) return null;

            var userDict = new Dictionary<string, object> { ["headers"] = headers, ["sampleRows"] = sampleRows };
            Dictionary<string, object> parsed = RunJsonDecision("excel-columns", cfg, ExcelColumnsSystemPrompt, Json.Serialize(userDict));
            if (parsed == null) return null;

            var mapping = Json.GetDict(parsed, "mapping");
            if (mapping == null) return null;
            double confidence = Json.GetDouble(parsed, "confidence") ?? 0;

            // Guard against the model naming a header it was never actually given.
            var valid = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
            string rawPkg = Json.GetString(mapping, "package");
            string rawLvl = Json.GetString(mapping, "level");
            string rawNum = Json.GetString(mapping, "sheetNumber");
            string rawNam = Json.GetString(mapping, "sheetName");
            string pkg = rawPkg != null && valid.Contains(rawPkg) ? rawPkg : null;
            string lvl = rawLvl != null && valid.Contains(rawLvl) ? rawLvl : null;
            string num = rawNum != null && valid.Contains(rawNum) ? rawNum : null;
            string nam = rawNam != null && valid.Contains(rawNam) ? rawNam : null;
            bool droppedRequired = pkg != rawPkg || lvl != rawLvl;

            return new ExcelColumnMapping
            {
                Package = pkg,
                Level = lvl,
                SheetNumber = num,
                SheetName = nam,
                Confidence = droppedRequired ? Math.Min(confidence, 0.4) : confidence
            };
        }

        // ---------------------------------------------------------------------------------------
        // 1b) Level naming: model vs drawing register
        // ---------------------------------------------------------------------------------------

        public sealed class LevelMapping
        {
            public Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public double Confidence;
        }

        private const string LevelMappingSystemPrompt =
            "You match Revit level names to how those same levels are written in an architectural drawing " +
            "register. The two often use different conventions — e.g. the model says \"-01-BF04-PVL\" or " +
            "\"00-GROUND\" where the register says \"BS04\" or \"GFL\". Use the ordering and the numbering " +
            "of the levels to work out the correspondence (basements count downward, then ground, then " +
            "floors upward). Reply with ONLY this JSON object, no prose, no markdown fences: " +
            "{\"map\":{\"<revit level name>\":\"<register wording, or null>\"},\"confidence\":<0 to 1>}. " +
            "Every value MUST be text that literally appears inside one of the register titles you were " +
            "given — never invent a label. Use null for any level you cannot place confidently.";

        /// <summary>Proposes, for each Revit level, the wording the register uses for it. Returns null
        /// on any failure, with 'unavailableReason' set to a sentence worth showing the user — a silent
        /// empty dialog is indistinguishable from "the AI had nothing to say", which wastes their time.</summary>
        public static LevelMapping DecideLevelMapping(List<string> revitLevels, List<string> registerTitles,
            out string unavailableReason)
        {
            unavailableReason = null;
            AIConAiConfig cfg = AIConAiConfig.Load();
            if (!cfg.UseAiDecisions)
            {
                unavailableReason = "AI-assisted decisions are turned off in AR400 Settings.";
                return null;
            }
            if (revitLevels == null || revitLevels.Count == 0 || registerTitles == null || registerTitles.Count == 0) return null;

            var userDict = new Dictionary<string, object>
            {
                ["revitLevels"] = revitLevels,
                ["registerTitles"] = registerTitles
            };
            Dictionary<string, object> parsed = RunJsonDecision("level-mapping", cfg, LevelMappingSystemPrompt, Json.Serialize(userDict));
            if (parsed == null) { unavailableReason = s_lastReason; return null; }

            Dictionary<string, object> map = Json.GetDict(parsed, "map");
            if (map == null) { unavailableReason = "The AI replied but not in the expected format."; return null; }

            var result = new LevelMapping { Confidence = Json.GetDouble(parsed, "confidence") ?? 0 };
            foreach (KeyValuePair<string, object> kv in map)
            {
                string label = kv.Value == null ? null : Convert.ToString(kv.Value);
                if (string.IsNullOrWhiteSpace(label)) continue;
                result.Map[kv.Key] = label.Trim();
            }
            return result.Map.Count > 0 ? result : null;
        }

        // ---------------------------------------------------------------------------------------
        // 2) Tag placement conflict resolution
        // ---------------------------------------------------------------------------------------

        public sealed class TagPositionChoice { public int ChosenIndex; public double Confidence; }

        private const string TagPositionSystemPrompt =
            "You choose the best placement for a Revit annotation tag so it avoids overlapping existing " +
            "tags/annotations already in the view. You get the tag's own bounding box, the bounding boxes " +
            "already occupied nearby, and a numbered list of candidate positions (index 0 is usually the " +
            "default, un-nudged position). Pick the candidate whose box (same size as tagBounds, centered " +
            "at that position) overlaps the FEWEST occupied boxes, preferring the candidate closest to " +
            "index 0 on a tie. Reply with ONLY this JSON object, no prose: " +
            "{\"chosenIndex\":<index into candidatePositions>,\"confidence\":<0 to 1>}.";

        /// <summary>Returns null on any failure — caller must fall back to its own default offset/nudge rule.</summary>
        public static TagPositionChoice DecideTagPosition(Dictionary<string, object> tagBounds,
            List<object> occupiedBounds, List<object> candidatePositions)
        {
            AIConAiConfig cfg = AIConAiConfig.Load();
            if (!cfg.UseAiDecisions) return null;

            var userDict = new Dictionary<string, object>
            {
                ["tagBounds"] = tagBounds,
                ["occupiedBounds"] = occupiedBounds,
                ["candidatePositions"] = candidatePositions
            };
            Dictionary<string, object> parsed = RunJsonDecision("tag-position", cfg, TagPositionSystemPrompt, Json.Serialize(userDict));
            if (parsed == null) return null;

            int? idx = Json.GetInt(parsed, "chosenIndex");
            if (!idx.HasValue || idx.Value < 0 || idx.Value >= candidatePositions.Count) return null;
            return new TagPositionChoice { ChosenIndex = idx.Value, Confidence = Json.GetDouble(parsed, "confidence") ?? 0 };
        }

        // ---------------------------------------------------------------------------------------
        // 3) Dimension face-pair selection (only for genuinely ambiguous wall pairs)
        // ---------------------------------------------------------------------------------------

        public sealed class DimensionFaceChoice { public int ChosenPairIndex; public double Confidence; }

        private const string DimensionFacesSystemPrompt =
            "You choose which pair of wall faces two walls should be dimensioned between, for an " +
            "architectural shop drawing. You get the two wall element ids and a numbered list of candidate " +
            "face pairs, each with the perpendicular distance between them in millimetres. Prefer the pair " +
            "representing the real, buildable clear gap between the two walls' NEAREST faces — usually the " +
            "shortest plausible distance, not an unrelated far pair. Reply with ONLY this JSON object, no " +
            "prose: {\"chosenPairIndex\":<index into candidateFacePairs>,\"confidence\":<0 to 1>}.";

        /// <summary>Returns null on any failure — caller must fall back to the two closest faces.</summary>
        public static DimensionFaceChoice DecideDimensionFaces(int wallAId, int wallBId, List<object> candidateFacePairs)
        {
            AIConAiConfig cfg = AIConAiConfig.Load();
            if (!cfg.UseAiDecisions) return null;

            var userDict = new Dictionary<string, object>
            {
                ["wallAId"] = wallAId, ["wallBId"] = wallBId, ["candidateFacePairs"] = candidateFacePairs
            };
            Dictionary<string, object> parsed = RunJsonDecision("dimension-faces", cfg, DimensionFacesSystemPrompt, Json.Serialize(userDict));
            if (parsed == null) return null;

            int? idx = Json.GetInt(parsed, "chosenPairIndex");
            if (!idx.HasValue || idx.Value < 0 || idx.Value >= candidateFacePairs.Count) return null;
            return new DimensionFaceChoice { ChosenPairIndex = idx.Value, Confidence = Json.GetDouble(parsed, "confidence") ?? 0 };
        }

        // ---------------------------------------------------------------------------------------
        // Shared plumbing: call the primary provider, retry once with a stricter reminder if the
        // JSON doesn't parse/validate, then try the fallback provider once. Every attempt is logged.
        // ---------------------------------------------------------------------------------------

        // Why the last decision came back empty, in words worth showing a user. Set by the paths below
        // and read straight after the call by the (single-threaded, UI-driven) caller.
        private static string s_lastReason;

        private static Dictionary<string, object> RunJsonDecision(string endpoint, AIConAiConfig cfg, string systemPrompt, string userPrompt)
        {
            s_lastReason = null;
            // Preferred path: reuse whichever AI the user already set up for the AICon chat panel
            // (aiconagent.json — their Gemini / DeepSeek / local model). Asking for a SEPARATE key just
            // for these background decisions was redundant; ar400ai.json now only exists to point
            // decisions at a different (e.g. cheaper) model than the chat, and is entirely optional.
            if (!cfg.HasPrimaryKey && !cfg.HasFallbackKey)
                return ViaChatPanelProvider(endpoint, cfg.UseProfile, systemPrompt, userPrompt);

            if (cfg.HasPrimaryKey)
            {
                Dictionary<string, object> r = TryOnce(endpoint, cfg.Provider, cfg.Model, cfg.ApiKey, cfg.BaseUrl, systemPrompt, userPrompt, false);
                if (r != null) return r;
                r = TryOnce(endpoint, cfg.Provider, cfg.Model, cfg.ApiKey, cfg.BaseUrl, systemPrompt, userPrompt, true);
                if (r != null) return r;
            }
            if (cfg.HasFallbackKey)
            {
                Dictionary<string, object> r = TryOnce(endpoint, cfg.FallbackProvider, cfg.FallbackModel, cfg.FallbackApiKey, cfg.FallbackBaseUrl, systemPrompt, userPrompt, false);
                if (r != null) return r;
            }
            return null;
        }

        // Runs the decision through the SAME provider stack the in-Revit chat panel uses, so the user's
        // existing Gemini / DeepSeek / local-model setup answers these questions with no extra account,
        // key or configuration. Falls back to null (caller asks the user) if nothing is configured.
        private static Dictionary<string, object> ViaChatPanelProvider(string endpoint, string profileKey,
            string systemPrompt, string userPrompt)
        {
            var sw = Stopwatch.StartNew();
            string label = "chat-panel";
            try
            {
                string usedPath;
                // Blank profileKey → ForProfile(null) falls back to the chat panel's active agent.
                Agent.Config agentCfg = Agent.Config.Load(null, out usedPath)
                    .ForProfile(string.IsNullOrWhiteSpace(profileKey) ? null : profileKey);
                if (agentCfg == null) return null;
                label = agentCfg.Provider + "/" + agentCfg.Model;

                string agentName = string.IsNullOrWhiteSpace(profileKey) ? "the AI Chat agent" : Agent.Config.Label(profileKey);

                // A cloud provider with no key can't answer; a local endpoint needs none.
                if (Agent.Config.RequiresApiKey(agentCfg) && string.IsNullOrWhiteSpace(agentCfg.ResolveApiKey()))
                {
                    s_lastReason = "No API key is set for " + agentName + ".";
                    Log(endpoint, label, agentCfg.Model, 0, 0, false, "no API key configured for the chat provider");
                    return null;
                }

                Agent.IProvider provider = Agent.ProviderFactory.Create(agentCfg);
                var messages = new List<Agent.ChatMessage>
                {
                    Agent.ChatMessage.System(systemPrompt),
                    Agent.ChatMessage.User(userPrompt)
                };

                // No tools: this is a single structured question, not an agent loop. Both providers
                // omit the tools field entirely when the array is empty.
                //
                // This call runs synchronously and can happen while a Revit transaction is open (see
                // WallDimensioning.cs), so it MUST be bounded to CallTimeout — NOT the provider's own
                // HttpClient.Timeout, which defaults to 300s (shared/OpenAiCompatibleProvider.cs:35,
                // shared/GeminiProvider.cs:45). CancellationToken.None here let a slow/hung local model
                // freeze Revit with a transaction open for up to 5 minutes. A cancelled call surfaces as
                // a TaskCanceledException, which the catch below (via FriendlyFailure) already turns
                // into a clean "took too long to answer" message — no new error handling needed here.
                Agent.AssistantTurn turn;
                using (var callCts = new System.Threading.CancellationTokenSource(CallTimeout))
                {
                    turn = provider
                        .CompleteAsync(messages, new System.Text.Json.Nodes.JsonArray(), callCts.Token)
                        .GetAwaiter().GetResult();
                }

                // The reply arrived — a parse failure from here on is the MODEL's fault, not the
                // connection's, and deserves its own message (Json.DeserializeObject throws on bad
                // input rather than returning null, so it needs its own catch).
                string replyText = turn != null ? (turn.Text ?? "") : "";
                Dictionary<string, object> parsed = null;
                try { parsed = Json.DeserializeObject(ExtractJsonObject(replyText)); }
                catch { parsed = null; }

                sw.Stop();
                if (parsed == null)
                    s_lastReason = agentName + " (" + agentCfg.Model + ") replied, but not in the JSON format asked for. " +
                                   "Small local models often can't manage this — switch AR400 Settings → Ask to Gemini " +
                                   "(free tier) for a reliable answer.";
                Log(endpoint, label, agentCfg.Model, parsed != null ? (Json.GetDouble(parsed, "confidence") ?? 0) : 0,
                    sw.ElapsedMilliseconds, parsed != null, parsed == null ? "reply was not valid JSON" : null);
                return parsed;
            }
            catch (Exception ex)
            {
                sw.Stop();
                s_lastReason = FriendlyFailure(ex, profileKey);
                Log(endpoint, label, "", 0, sw.ElapsedMilliseconds, false, ex.Message);
                return null;
            }
        }

        // Turns a raw exception into something a BIM user can act on. The common one by far is a local
        // model selected while Ollama isn't running: the request fails to connect in a few seconds and
        // the raw message ("An error occurred while sending the request.") explains nothing.
        private static string FriendlyFailure(Exception ex, string profileKey)
        {
            string agentName = string.IsNullOrWhiteSpace(profileKey) ? "the AI Chat agent" : Agent.Config.Label(profileKey);
            bool isLocal = string.Equals(profileKey, Agent.Config.ProfileLocal, StringComparison.OrdinalIgnoreCase);

            bool connectionFailed = ex is System.Net.Http.HttpRequestException ||
                                    ex.InnerException is System.Net.Sockets.SocketException ||
                                    (ex.Message ?? "").IndexOf("sending the request", StringComparison.OrdinalIgnoreCase) >= 0;

            if (connectionFailed && isLocal)
                return "Couldn't reach the local model — Ollama doesn't appear to be running. " +
                       "Start it (run \"ollama serve\", or open the Ollama app) and try again, " +
                       "or switch AR400 Settings → Ask to Gemini.";
            if (connectionFailed)
                return "Couldn't reach " + agentName + " (no network response). Check the connection, " +
                       "or pick a different agent in AR400 Settings → Ask.";
            if (ex is TaskCanceledException)
                return agentName + " took too long to answer.";
            return agentName + " couldn't answer: " + ex.Message;
        }

        private static Dictionary<string, object> TryOnce(string endpoint, string provider, string model, string apiKey,
            string baseUrl, string systemPrompt, string userPrompt, bool strictReminder)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string system = strictReminder
                    ? systemPrompt + " IMPORTANT: your previous reply was not valid JSON matching the schema — return ONLY a single valid JSON object."
                    : systemPrompt;

                string raw = CallProvider(provider, model, apiKey, baseUrl, system, userPrompt);
                string jsonText = ExtractJsonObject(raw);
                Dictionary<string, object> parsed = Json.DeserializeObject(jsonText);
                sw.Stop();
                Log(endpoint, provider, model, parsed != null ? (Json.GetDouble(parsed, "confidence") ?? 0) : 0, sw.ElapsedMilliseconds, parsed != null, null);
                return parsed;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log(endpoint, provider, model, 0, sw.ElapsedMilliseconds, false, ex.Message);
                return null;
            }
        }

        private static string CallProvider(string provider, string model, string apiKey, string baseUrl, string system, string user)
        {
            bool custom = !string.IsNullOrWhiteSpace(baseUrl);
            switch ((provider ?? "").Trim().ToLowerInvariant())
            {
                case "openrouter":
                    return CallOpenAiCompatible(custom ? baseUrl.TrimEnd('/') : "https://openrouter.ai/api/v1/chat/completions",
                        model, apiKey, system, user);
                case "anthropic":
                    return CallAnthropic(custom ? baseUrl.TrimEnd('/') : "https://api.anthropic.com/v1/messages",
                        model, apiKey, system, user);
                default:
                    throw new InvalidOperationException("Unknown AI provider '" + provider + "' (expected openrouter or anthropic).");
            }
        }

        private static string CallOpenAiCompatible(string url, string model, string apiKey, string system, string user)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = system },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = user }
                }
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("Authorization", "Bearer " + apiKey);
                request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = Http.SendAsync(request).GetAwaiter().GetResult())
                {
                    string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException("HTTP " + (int)resp.StatusCode + ": " + Truncate(respText, 300));

                    Dictionary<string, object> respDict = Json.DeserializeObject(respText);
                    List<object> choices = Json.GetList(respDict, "choices");
                    var first = choices != null && choices.Count > 0 ? choices[0] as Dictionary<string, object> : null;
                    Dictionary<string, object> message = first != null ? Json.GetDict(first, "message") : null;
                    return message != null ? Json.GetString(message, "content", "") : "";
                }
            }
        }

        private static string CallAnthropic(string url, string model, string apiKey, string system, string user)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = 512,
                ["temperature"] = 0,
                ["system"] = system,
                ["messages"] = new List<object> { new Dictionary<string, object> { ["role"] = "user", ["content"] = user } }
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = Http.SendAsync(request).GetAwaiter().GetResult())
                {
                    string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException("HTTP " + (int)resp.StatusCode + ": " + Truncate(respText, 300));

                    Dictionary<string, object> respDict = Json.DeserializeObject(respText);
                    List<object> contentList = Json.GetList(respDict, "content");
                    var first = contentList != null && contentList.Count > 0 ? contentList[0] as Dictionary<string, object> : null;
                    return first != null ? Json.GetString(first, "text", "") : "";
                }
            }
        }

        // ONE shared HttpClient for the process. Creating (and disposing) one per call leaves sockets
        // in TIME_WAIT and eventually exhausts them — the classic .NET HttpClient mistake. Per-call
        // headers are therefore set on the request, never on the client.
        private static readonly HttpClient Http = new HttpClient { Timeout = CallTimeout };

        // Models sometimes wrap JSON in ```json fences or add a sentence before/after — grab the outermost {...}.
        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text.Substring(start, end - start + 1) : text;
        }

        private static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private static readonly string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "decisions.log");

        private static void Log(string endpoint, string provider, string model, double confidence, long latencyMs, bool ok, string note)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                var entry = new Dictionary<string, object>
                {
                    ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["endpoint"] = endpoint,
                    ["provider"] = provider,
                    ["model"] = model,
                    ["confidence"] = confidence,
                    ["latencyMs"] = latencyMs,
                    ["ok"] = ok
                };
                if (note != null) entry["note"] = note;
                File.AppendAllText(LogPath, Json.Serialize(entry) + Environment.NewLine);
            }
            catch { /* logging must never break a decision call */ }
        }
    }
}
