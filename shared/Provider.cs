#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace AICon.Agent
{
    // The swappable seam of AICon. Any AI backend — a local model (Ollama, LM Studio), or a cloud
    // one (Gemini, ChatGPT, DeepSeek) — is plugged in behind IProvider. Everything below this
    // interface (the executor, the tool catalogue, the agent loop) is identical regardless of which
    // model drives it.
    public interface IProvider
    {
        string Name { get; }

        // Optional status sink for transient conditions the caller may want to show (e.g. "rate
        // limited — retrying in 30s"). Null by default; the console and the panel set it.
        Action<string>? Status { get; set; }

        // Given the running conversation and the tool catalogue (MCP-shape:
        // [{ name, description, inputSchema }, ...]), produce the assistant's next turn:
        // either some text to show the user, one or more tool calls to run, or both.
        Task<AssistantTurn> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, JsonArray tools, CancellationToken ct);
    }

    public enum Role { System, User, Assistant, Tool }

    // A binary attachment carried on a user message: an image or a PDF, base64-encoded.
    // Gemini reads both natively; OpenAI-compatible endpoints read images (on vision models).
    // Spreadsheets are NOT sent this way — their text is extracted and inlined into Content.
    public sealed class ChatAttachment
    {
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "";    // image/png, image/jpeg, application/pdf, …
        public string Base64Data { get; set; } = "";
    }

    // One entry in the conversation. For an assistant turn that wants to run tools, ToolCalls is
    // populated. For a tool result being fed back, Role=Tool with ToolCallId + Content.
    public sealed class ChatMessage
    {
        public Role Role { get; set; }
        public string? Content { get; set; }
        public List<ToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }   // set on Role.Tool: which call this answers
        public string? Name { get; set; }          // set on Role.Tool: the tool name
        public List<ChatAttachment>? Attachments { get; set; }   // user messages only

        public static ChatMessage System(string text) => new ChatMessage { Role = Role.System, Content = text };
        public static ChatMessage User(string text) => new ChatMessage { Role = Role.User, Content = text };
    }

    // A single tool invocation requested by the model. ArgumentsJson is the raw JSON object of
    // arguments as a string (that is how OpenAI-compatible APIs return it).
    // ThoughtSignature is Gemini-only: thinking models attach an opaque signature to each functionCall
    // part that MUST be echoed back on the next turn, or the API rejects the request. Other providers
    // leave it null and ignore it.
    public sealed class ToolCall
    {
        public string Id { get; }
        public string Name { get; }
        public string ArgumentsJson { get; }
        public string? ThoughtSignature { get; }

        public ToolCall(string id, string name, string argumentsJson, string? thoughtSignature = null)
        {
            Id = id;
            Name = name;
            ArgumentsJson = argumentsJson;
            ThoughtSignature = thoughtSignature;
        }
    }

    // The model's reply for one turn.
    public sealed class AssistantTurn
    {
        public string? Text { get; }
        public List<ToolCall> ToolCalls { get; }
        public bool WantsTools => ToolCalls.Count > 0;

        public AssistantTurn(string? text, List<ToolCall> toolCalls)
        {
            Text = text;
            ToolCalls = toolCalls;
        }
    }
}
