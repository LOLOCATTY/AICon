using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;

namespace AICon
{
    /// <summary>A single tool request travelling from the HTTP thread to the Revit UI thread.</summary>
    internal class BridgeJob
    {
        public string Tool;
        public Dictionary<string, object> Args;
        public string ResultJson;
        // Which front door this call came from ("mcp" or "in_revit_panel") — the closest honest answer
        // AICon has to "who" for the audit log (AuditLog.cs), since there is no user/session identity
        // anywhere in this product. Set by the caller that creates the job; defaults to "unknown" if a
        // caller forgets, rather than failing the call over a missing label.
        public string Source = "unknown";
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>Runs queued jobs inside Revit's API context.</summary>
    internal class RevitEventHandler : IExternalEventHandler
    {
        public readonly ConcurrentQueue<BridgeJob> Queue = new ConcurrentQueue<BridgeJob>();

        public void Execute(UIApplication app)
        {
            while (Queue.TryDequeue(out BridgeJob job))
            {
                try
                {
                    object data = ToolDispatcher.Dispatch(app, job.Tool, job.Args ?? new Dictionary<string, object>(), job.Source);
                    job.ResultJson = Json.Serialize(new Dictionary<string, object> { { "ok", true }, { "data", data } });
                }
                catch (Exception ex)
                {
                    job.ResultJson = Json.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "error", ex.InnerException != null ? ex.InnerException.Message : ex.Message }
                    });
                }
                finally
                {
                    job.Done.Set();
                }
            }
        }

        public string GetName() { return "AICon Bridge"; }
    }

    /// <summary>Localhost HTTP server that Claude's MCP process talks to.</summary>
    internal class BridgeServer
    {
        private readonly int _port;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _event;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _stopping;
        public long RequestCount;

        // Shared secret every caller must send as the 'X-AICon-Token' header. Without this, ANY local
        // process — not just Claude Desktop / the AICon MCP server — could POST to this port and drive
        // Revit (including run_code). Generated once per machine and persisted so the MCP server process
        // (a separate .exe, possibly started before or after Revit) can read the same value.
        public readonly string Token;
        private static string TokenPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "bridge.token");

        public bool IsRunning { get { return _listener != null && _listener.IsListening; } }

        public BridgeServer(int port, RevitEventHandler handler, ExternalEvent externalEvent)
        {
            _port = port;
            _handler = handler;
            _event = externalEvent;
            Token = LoadOrCreateToken();
        }

        private static string LoadOrCreateToken()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TokenPath));
                if (File.Exists(TokenPath))
                {
                    string existing = File.ReadAllText(TokenPath).Trim();
                    if (existing.Length >= 32) return existing;
                }
            }
            catch (Exception ex) { App.Log("Could not read bridge token, minting a new one: " + ex.Message); }

            string token = GenerateToken();
            try { File.WriteAllText(TokenPath, token, new UTF8Encoding(false)); }
            catch (Exception ex) { App.Log("Could not persist bridge token (it will change every restart): " + ex.Message); }
            return token;
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + _port + "/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "AIConBridge" };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            try { _listener?.Stop(); } catch { }
        }

        private void Loop()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                try { Handle(ctx); }
                catch (Exception ex) { App.Log("Request error: " + ex.Message); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref RequestCount);
            string responseJson;

            if (ctx.Request.HttpMethod == "GET")
            {
                // Status ping only — no model access, no token required, so a quick curl/browser check
                // keeps working per the README troubleshooting section.
                responseJson = "{\"ok\":true,\"data\":\"AICon bridge is running\"}";
            }
            else if (!string.Equals(ctx.Request.Headers["X-AICon-Token"], Token, StringComparison.Ordinal))
            {
                responseJson = Json.Serialize(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", "Missing or wrong bridge token. Every tool call must send the token in " +
                               TokenPath + " as the 'X-AICon-Token' header." }
                });
            }
            else
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();

                var job = new BridgeJob { Source = "mcp" };
                try
                {
                    var request = Json.DeserializeObject(body);
                    job.Tool = Json.GetString(request, "tool");
                    job.Args = Json.GetDict(request, "args") ?? new Dictionary<string, object>();
                }
                catch (Exception ex)
                {
                    job = null;
                    responseJson = Json.Serialize(new Dictionary<string, object>
                        { { "ok", false }, { "error", "Bad request JSON: " + ex.Message } });
                    Write(ctx, responseJson);
                    return;
                }

                _handler.Queue.Enqueue(job);
                _event.Raise();

                // The external event only fires when Revit is idle (no modal dialog, no active edit mode).
                if (job.Done.Wait(TimeSpan.FromSeconds(110)))
                {
                    responseJson = job.ResultJson;
                }
                else
                {
                    responseJson = Json.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "error", "Revit did not respond in time. It may be busy, showing a dialog, or in an active edit mode. Finish what Revit is doing and try again." }
                    });
                }
            }

            Write(ctx, responseJson);
        }

        private static void Write(HttpListenerContext ctx, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? "{\"ok\":false,\"error\":\"empty response\"}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}

