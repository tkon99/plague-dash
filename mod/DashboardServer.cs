using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PlagueDash
{
    /// <summary>
    /// A tiny embedded web server that serves the dashboard UI (from embedded
    /// resources baked into the DLL) and live run data (from <see cref="LiveState"/>)
    /// via Server-Sent Events. No disk files, no external dependencies beyond the
    /// System reference.
    ///
    /// Runs on a single background thread. Two routes:
    ///   GET /events            → SSE stream: replay history, then push live samples.
    ///   GET /, /style.css, ... → the embedded dashboard resources.
    ///
    /// Start/stop is wired into UMM's OnToggle so toggling the mod cleanly starts
    /// and stops the server thread.
    /// </summary>
    internal static class DashboardServer
    {
        private static Thread _thread;
        private static volatile bool _stopping;
        private static int _port = 8765;
        private static TcpListener _listener;

        // Set true once the listener is bound and accepting, so callers know it's
        // safe to open a browser. Reset on Stop.
        private static volatile bool _listening;
        private static readonly AutoResetEvent _readySignal = new AutoResetEvent(false);

        // Connected SSE clients, so we can drop them all on shutdown.
        private static readonly List<TcpClient> _sseClients = new List<TcpClient>();
        private static readonly object _clientLock = new object();

        public static void Start(int port)
        {
            _port = port;
            _stopping = false;
            _listening = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "PlagueDash.Server" };
            _thread.Start();
        }

        /// <summary>Block until the server has bound its port (or timeout). Returns
        /// true if listening. Use before launching a browser so it doesn't connect
        /// before the listener is ready.</summary>
        public static bool WaitUntilListening(int timeoutMs = 3000)
        {
            if (_listening) return true;
            return _readySignal.WaitOne(timeoutMs) && _listening;
        }

        public static void Stop()
        {
            _stopping = true;
            _listening = false;
            try { _listener?.Stop(); } catch { /* ignore */ }
            // closing the listener unblocks AcceptTcpClient; also kick SSE clients
            lock (_clientLock)
            {
                foreach (var c in _sseClients) { try { c.Close(); } catch { } }
                _sseClients.Clear();
            }
            _thread?.Join(2000);
        }

        /// <summary>Open the dashboard in the user's default browser. Called once
        /// the server is confirmed listening, so the browser doesn't connect too
        /// early. Tries several launch methods for robustness under Unity/Mono.</summary>
        public static void OpenInBrowser(int port)
        {
            var url = "http://localhost:" + port + "/";

            // Method 1: the simple Process.Start(url) form. On Windows .NET this
            // ShellExecutes to the default browser; Unity Mono also handles it.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                Main.Log("Opened dashboard in browser: " + url);
                return;
            }
            catch (Exception e) { Main.Log("Browser launch method 1 failed: " + e.Message); }

            // Method 2: shell out to cmd /c start, which always opens the default
            // browser on Windows. Robust fallback if Mono's Process.Start misbehaves.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = "/c start \"\" \"" + url + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Main.Log("Opened dashboard via cmd start: " + url);
                return;
            }
            catch (Exception e) { Main.Log("Browser launch method 2 failed: " + e.Message); }

            Main.Log("Could not open browser automatically — open " + url + " manually.");
        }

        private static void Run()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _listening = true;
                _readySignal.Set();
                Main.Log($"Dashboard server listening on http://localhost:{_port}");
                while (!_stopping)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (SocketException) { break; } // listener stopped
                    // Handle each connection on a short-lived thread so a slow/idle
                    // SSE client never blocks new connections.
                    var t = new Thread(() => HandleClient(client))
                    {
                        IsBackground = true,
                        Name = "PlagueDash.Conn"
                    };
                    t.Start();
                }
            }
            catch (Exception e)
            {
                if (!_stopping) Main.Log($"Dashboard server error: {e.Message}");
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = client.SendTimeout = 5000;
                    // Read the request line (and headers, which we ignore) up to the
                    // first blank line. Cap reading so a malformed client can't hang us.
                    var sr = new StreamReader(stream, new UTF8Encoding(false));
                    string requestLine = sr.ReadLine();
                    if (requestLine == null) return;

                    // skip headers
                    var headers = new List<string>();
                    string h;
                    int guard = 0;
                    while ((h = sr.ReadLine()) != null && h.Length > 0 && guard++ < 50)
                        headers.Add(h);

                    // Parse "GET <path> HTTP/1.1"
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteStatus(stream, 405, "Method Not Allowed");
                        return;
                    }
                    string path = parts[1].Split('?')[0];

                    if (path == "/events")
                    {
                        ServeSSE(client, stream);
                    }
                    else
                    {
                        ServeResource(stream, path);
                    }
                }
            }
            catch { /* client disconnect mid-request — ignore */ }
        }

        // --- SSE: replay history, then stream live updates until disconnect ---
        private static void ServeSSE(TcpClient client, NetworkStream stream)
        {
            // SSE requires Content-Type: text/event-stream and no buffering.
            var head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: text/event-stream\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: keep-alive\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Flush();

            lock (_clientLock) _sseClients.Add(client);

            bool firstSse = true;            // per-connection: send full history first time
            int lastHistoryCount = 0;        // track how many samples we've already sent
            int lastCountryDay = -1;         // track which country snapshot we've sent
            int lastPurchaseCount = 0;       // track how many purchases we've already sent
            long lastMetaVersion = -1;
            TechEntry[] lastTechs = null;    // track the techs list (reference compare)
            int lastArchiveCount = 0;        // track how many archived runs we've sent
            try
            {
                while (!_stopping && client.Connected)
                {
                    long ver = LiveState.Version;
                    if (ver != lastMetaVersion)
                    {
                        lastMetaVersion = ver;
                        var (metaJson, history, countries, countryDay, purchases) = LiveState.Snapshot();
                        var techs = LiveState.Techs;

                        // On first connect (or after a reset): send meta + ALL history + purchases.
                        if (firstSse)
                        {
                            SendSSE(stream, "meta", metaJson);
                            foreach (var s in history) SendSSE(stream, "sample", s.ToJson());
                            lastHistoryCount = history.Count;
                            if (countries != null)
                            {
                                SendSSE(stream, "countries", CountrySnapshot.ToJson(countryDay, countries));
                                lastCountryDay = countryDay;
                            }
                            foreach (var p in purchases) SendSSE(stream, "purchase", p.ToJson());
                            lastPurchaseCount = purchases.Count;
                            if (techs != null && techs.Length > 0)
                            {
                                SendSSE(stream, "techs", TechsToJson(techs));
                                lastTechs = techs;
                            }
                            // Send the archived-run session history (all of them).
                            var archives = LiveState.Archives();
                            if (archives.Count > 0)
                            {
                                foreach (var a in archives) SendSSE(stream, "archive", RunArchiveToJson(a));
                                lastArchiveCount = archives.Count;
                            }
                            firstSse = false;
                        }
                        else
                        {
                            // Incremental: send any newly-added samples.
                            if (history.Count > lastHistoryCount)
                            {
                                for (int i = lastHistoryCount; i < history.Count; i++)
                                    SendSSE(stream, "sample", history[i].ToJson());
                                lastHistoryCount = history.Count;
                            }
                            // A history reset (new run) shows as count < last → resend all.
                            else if (history.Count < lastHistoryCount)
                            {
                                foreach (var s in history) SendSSE(stream, "sample", s.ToJson());
                                lastHistoryCount = history.Count;
                            }
                            // New country snapshot?
                            if (countries != null && countryDay != lastCountryDay)
                            {
                                SendSSE(stream, "countries", CountrySnapshot.ToJson(countryDay, countries));
                                lastCountryDay = countryDay;
                            }
                            // Incremental purchase events (and handle feed reset on new run).
                            if (purchases.Count < lastPurchaseCount)
                            {
                                foreach (var p in purchases) SendSSE(stream, "purchase", p.ToJson());
                                lastPurchaseCount = purchases.Count;
                            }
                            else if (purchases.Count > lastPurchaseCount)
                            {
                                for (int i = lastPurchaseCount; i < purchases.Count; i++)
                                    SendSSE(stream, "purchase", purchases[i].ToJson());
                                lastPurchaseCount = purchases.Count;
                            }
                            // Trait Planner list changed (new array = new data)?
                            if (techs != null && !ReferenceEquals(techs, lastTechs))
                            {
                                SendSSE(stream, "techs", TechsToJson(techs));
                                lastTechs = techs;
                            }
                            // A new run was archived?
                            var archives = LiveState.Archives();
                            if (archives.Count != lastArchiveCount)
                            {
                                for (int i = lastArchiveCount; i < archives.Count; i++)
                                    SendSSE(stream, "archive", RunArchiveToJson(archives[i]));
                                lastArchiveCount = archives.Count;
                            }
                            // Meta (status may change to won/lost).
                            SendSSE(stream, "meta", metaJson);
                        }
                    }
                    Thread.Sleep(100); // poll cadence; fine for human-viewed charts
                }
            }
            catch { /* client gone */ }
            finally
            {
                lock (_clientLock) _sseClients.Remove(client);
            }
        }

        // Serve static embedded resources by URL path.
        private static void ServeResource(NetworkStream stream, string path)
        {
            // map URL → embedded resource logical name
            string logicalName;
            string mime;
            switch (path)
            {
                case "/":
                case "/index.html":
                    logicalName = "PlagueDash.dashboard.index.html"; mime = "text/html; charset=utf-8"; break;
                case "/style.css":
                    logicalName = "PlagueDash.dashboard.style.css"; mime = "text/css; charset=utf-8"; break;
                case "/app.js":
                    logicalName = "PlagueDash.dashboard.app.js"; mime = "application/javascript; charset=utf-8"; break;
                case "/chart.js":
                    logicalName = "PlagueDash.dashboard.chart.js"; mime = "application/javascript; charset=utf-8"; break;
                default:
                    WriteStatus(stream, 404, "Not Found");
                    return;
            }

            var asm = Assembly.GetExecutingAssembly();
            using (var rs = asm.GetManifestResourceStream(logicalName))
            {
                if (rs == null)
                {
                    WriteStatus(stream, 404, "Not Found");
                    Main.Log($"Embedded resource not found: {logicalName}. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
                    return;
                }
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: ").Append(mime).Append("\r\n");
                sb.Append("Content-Length: ").Append(rs.Length).Append("\r\n");
                sb.Append("Cache-Control: no-store\r\n");
                sb.Append("Connection: close\r\n\r\n");
                byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(head, 0, head.Length);
                rs.CopyTo(stream);
                stream.Flush();
            }
        }

        /// <summary>Serialize an archived run as one JSON object (meta + samples +
        /// purchases + countries + techs). Sent as an 'archive' SSE event.</summary>
        private static string RunArchiveToJson(RunArchive a)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"meta\":").Append(a.metaJson ?? "{}");
            sb.Append(",\"finalDay\":").Append(a.finalDay);
            sb.Append(",\"plagueType\":").Append(JsonStr(a.plagueType));
            sb.Append(",\"difficulty\":").Append(JsonStr(a.difficulty));
            sb.Append(",\"samples\":[");
            for (int i = 0; i < a.samples.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a.samples[i].ToJson());
            }
            sb.Append("],\"purchases\":[");
            for (int i = 0; i < a.purchases.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a.purchases[i].ToJson());
            }
            sb.Append("],\"countries\":");
            if (a.countries != null) sb.Append(CountrySnapshot.ToJson(a.countryDay, a.countries));
            else sb.Append("null");
            sb.Append(",\"techs\":");
            if (a.techs != null && a.techs.Length > 0) sb.Append(TechsToJson(a.techs));
            else sb.Append("null");
            sb.Append('}');
            return sb.ToString();
        }

        private static string TechsToJson(TechEntry[] techs)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < techs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(techs[i].ToJson());
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\');
                else if (c == '\n') { sb.Append("\\n"); continue; }
                else if (c == '\r') { sb.Append("\\r"); continue; }
                sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static void SendSSE(NetworkStream stream, string eventType, string json)
        {
            // event: <type>\ndata: <json>\n\n
            var frame = "event: " + eventType + "\ndata: " + json + "\n\n";
            byte[] b = Encoding.UTF8.GetBytes(frame);
            stream.Write(b, 0, b.Length);
            stream.Flush();
        }

        private static void WriteStatus(NetworkStream stream, int code, string reason)
        {
            var s = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            byte[] b = Encoding.UTF8.GetBytes(s);
            try { stream.Write(b, 0, b.Length); stream.Flush(); } catch { }
        }
    }
}
