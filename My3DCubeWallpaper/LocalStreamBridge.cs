using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace My3DCubeWallpaper
{
    public class LocalStreamBridge : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        public const int Port = 48124;

        public Func<int, int, string, Task<string?>>? OfferHandler { get; set; }
        public Action<int, int, string>? CandidateHandler { get; set; }
        public Action<int, int>? StopHandler { get; set; }
        public Func<object>? GetMonitorsHandler { get; set; }

        public bool IsRunning => _isRunning;

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _isRunning = true;
                _cts = new CancellationTokenSource();

                AppLogger.Log($"LocalStreamBridge started on http://127.0.0.1:{Port}/");
                Task.Run(() => ListenLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"LocalStreamBridge failed to start: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listener = null;
                AppLogger.Log("LocalStreamBridge stopped.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"LocalStreamBridge stop error: {ex.Message}");
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), token);
                }
                catch (HttpListenerException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        AppLogger.Log($"LocalStreamBridge accept error: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            // Enable CORS for Chrome extension
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            try
            {
                var path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "";

                if (req.HttpMethod == "GET" && (path == "/api/status" || path == "/"))
                {
                    await WriteJsonResponseAsync(res, 200, new
                    {
                        status = "ok",
                        bridge = "My3DCubeWallpaper LocalStreamBridge",
                        version = "1.0.0",
                        active = true
                    });
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/api/monitors")
                {
                    var monitors = GetMonitorsHandler?.Invoke() ?? Array.Empty<object>();
                    await WriteJsonResponseAsync(res, 200, new { monitors });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/offer")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    int faceIndex = root.TryGetProperty("faceIndex", out var fi) ? fi.GetInt32() : 0;
                    int monitorIndex = root.TryGetProperty("monitorIndex", out var mi) ? mi.GetInt32() : -1;
                    string sdp = root.TryGetProperty("sdp", out var s) ? s.GetString() ?? "" : "";

                    AppLogger.Log($"LocalStreamBridge received WebRTC Offer for face {faceIndex}, monitor {monitorIndex} ({sdp.Length} chars)");

                    string? answerSdp = null;
                    if (OfferHandler != null)
                    {
                        answerSdp = await OfferHandler.Invoke(faceIndex, monitorIndex, sdp);
                    }

                    if (!string.IsNullOrEmpty(answerSdp))
                    {
                        await WriteJsonResponseAsync(res, 200, new
                        {
                            type = "answer",
                            sdp = answerSdp,
                            faceIndex = faceIndex,
                            monitorIndex = monitorIndex
                        });
                    }
                    else
                    {
                        await WriteJsonResponseAsync(res, 504, new
                        {
                            error = "Timed out waiting for 3D Cube wallpaper to generate WebRTC answer."
                        });
                    }
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/candidate")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();

                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    int faceIndex = root.TryGetProperty("faceIndex", out var fi) ? fi.GetInt32() : 0;
                    int monitorIndex = root.TryGetProperty("monitorIndex", out var mi) ? mi.GetInt32() : -1;
                    var candidateJson = root.TryGetProperty("candidate", out var c) ? c.GetRawText() : "{}";

                    CandidateHandler?.Invoke(faceIndex, monitorIndex, candidateJson);

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/stop")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    int faceIndex = 0;
                    int monitorIndex = -1;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("faceIndex", out var fi))
                        {
                            faceIndex = fi.GetInt32();
                        }
                        if (doc.RootElement.TryGetProperty("monitorIndex", out var mi))
                        {
                            monitorIndex = mi.GetInt32();
                        }
                    }
                    catch { }

                    AppLogger.Log($"LocalStreamBridge received STOP for face {faceIndex}, monitor {monitorIndex}");
                    StopHandler?.Invoke(faceIndex, monitorIndex);

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                res.StatusCode = 404;
                res.Close();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"LocalStreamBridge request error: {ex.Message}");
                try
                {
                    await WriteJsonResponseAsync(res, 500, new { error = ex.Message });
                }
                catch { }
            }
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse res, int statusCode, object data)
        {
            res.StatusCode = statusCode;
            res.ContentType = "application/json";
            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            res.Close();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
