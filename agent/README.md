# AICon Agent — drive Revit with any AI model (local or cloud)

A self-contained console host that lets **any** AI model operate the live Revit model through the
existing AICon bridge. The model is a swappable part; the Revit tools and the agent loop stay the
same no matter which model you use.

```
You ─► AIConAgent ─► IProvider (local or cloud model)
               └────► Revit bridge (localhost:55234) ─► AICon add-in inside Revit
```

Same 77 Revit tools as the Claude/MCP build — one shared `ToolRegistry` feeds both, so the two
paths never drift.

## How it connects

`AIConAgent` talks to the model over the **OpenAI-compatible `/chat/completions` "tools" API** —
the de-facto standard shared by local runtimes *and* the mainstream clouds. Switching model is
just editing `aiconagent.json`; no rebuild.

| Backend | `provider` | `baseUrl` | key needed |
|---|---|---|---|
| **Ollama** (local) | `openai` | `http://localhost:11434/v1` | no |
| **LM Studio** (local) | `openai` | `http://localhost:1234/v1` | no |
| **DeepSeek** (cloud) | `openai` | `https://api.deepseek.com` | yes |
| **ChatGPT** (cloud) | `openai` | `https://api.openai.com/v1` | yes |
| **Gemini** (cloud) | `gemini` | *(leave blank — public API is the default)* | yes |

This is the path to the "own local model" goal: run **Qwen** or **Llama** in Ollama, point
`baseUrl` at it, and no project data ever leaves the machine.

## Config — `aiconagent.json`

Searched next to the exe, then `%APPDATA%\AICon\aiconagent.json`. Pass a path as the first
argument to override. If none exists, a starter file is written next to the exe on first run.

**Local model (Qwen via Ollama) — private, no key, no per-prompt cost:**
```json
{
  "provider": "openai",
  "baseUrl": "http://localhost:11434/v1",
  "model": "qwen2.5-coder:7b"
}
```

**Cloud (DeepSeek) — key read from an environment variable, never stored in the file:**
```json
{
  "provider": "openai",
  "baseUrl": "https://api.deepseek.com",
  "model": "deepseek-chat",
  "apiKeyEnv": "DEEPSEEK_API_KEY"
}
```

**Gemini — free key from Google AI Studio, read from an environment variable:**
```json
{
  "provider": "gemini",
  "model": "gemini-2.0-flash",
  "apiKeyEnv": "GEMINI_API_KEY"
}
```
Leave `baseUrl` out for Gemini: the public Generative Language API is used automatically (set it
only for a Vertex/proxy endpoint). Get a free key at <https://aistudio.google.com/apikey>.

Key resolution order: `apiKey` (if set) → the env var named by `apiKeyEnv` → none (fine for local).
Prefer `apiKeyEnv` so secrets stay out of the file.

## Run

1. Start Revit, open a project, confirm the **AICon** ribbon tab loaded (the bridge listens on
   `localhost:55234`).
2. For a local model: install [Ollama](https://ollama.com), then `ollama pull qwen2.5-coder:7b`.
3. Run the agent:
   ```bash
   dotnet run -c Release --project agent            # or run agent\bin\Release\net10.0\AIConAgent.exe
   ```
4. Type in plain language:
   ```
   you › what's in my model? any warnings?
   you › create a 4m x 3m room with 3m walls on Level 1
   ```
   `exit` / `quit` to leave.

## Tool-call accuracy note

Tool calling stops the model from inventing element ids or numbers — it must go through the real
Revit functions. It does **not** upgrade the model's planning ability: smaller local models pick
tool sequences and coordinates less reliably than a frontier model. The swappable-provider design
is the mitigation — start on the strongest backend available, and move to local models as they
improve, without changing anything below `IProvider`.

## Adding another backend

Implement `IProvider` (see [`Provider.cs`](Provider.cs)) and register it in the `switch` in
[`Program.cs`](Program.cs) — that's the entire change; the tool catalogue and Revit dispatch are
reused unchanged. `OpenAiCompatibleProvider` and `GeminiProvider` are the two working examples; an
Anthropic slot is the obvious next addition.

## Layout

| File | Role |
|---|---|
| `Program.cs` | console host + the agent loop (model ↔ tools) |
| `Provider.cs` | `IProvider` seam + neutral message/tool-call types |
| `OpenAiCompatibleProvider.cs` | OpenAI-compatible backend (Ollama, LM Studio, DeepSeek, ChatGPT) |
| `GeminiProvider.cs` | Google Gemini backend (generateContent + functionDeclarations) |
| `RevitBridge.cs` | POST `{tool,args}` to `localhost:55234` |
| `Config.cs` | `aiconagent.json` loading + key resolution |
| `..\shared\ToolRegistry.cs` | the 77 tools — shared with the MCP server |
