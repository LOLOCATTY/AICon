#nullable enable
using System;

namespace AICon.Agent
{
    // The single place that maps a config's "provider" string to an IProvider. Shared by the console
    // host and the in-Revit panel so a new backend is registered exactly once. Adding Anthropic (or
    // any other) is a one-line case here plus its provider class.
    public static class ProviderFactory
    {
        public static IProvider Create(Config cfg)
        {
            switch ((cfg.Provider ?? "").ToLowerInvariant())
            {
                case "openai":
                case "openai-compatible":
                case "ollama":
                case "lmstudio":
                case "deepseek":
                    return new OpenAiCompatibleProvider(cfg);
                case "gemini":
                case "google":
                    return new GeminiProvider(cfg);
                default:
                    throw new NotSupportedException(
                        "Unknown provider '" + cfg.Provider + "'. Supported: openai (OpenAI-compatible: " +
                        "Ollama, LM Studio, DeepSeek, ChatGPT), gemini.");
            }
        }
    }
}
