using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly object _broadcastLock = new();
        private static bool _isBroadcasting = false;
        private static string? _broadcastStreamId = null;
        private static string? _broadcastTitle = null;
        private static int _broadcastFaceIndex = -1;
        private static bool _broadcastAllFaces = true;
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _localSignalQueues = new();

        public Func<int, int, string, Task<string?>>? OfferHandler { get; set; }
        public Action<int, int, string>? CandidateHandler { get; set; }
        public Action<int, int>? StopHandler { get; set; }
        public Func<object>? GetMonitorsHandler { get; set; }

        // Window Caster extensions
        public WindowCaptureService? CaptureService { get; set; }
        public Action? ShowWindowCasterHandler { get; set; }
        public Func<int, bool, string, int, Task>? StartWindowCastHandler { get; set; }
        public Func<int, bool, int, Task>? StopWindowCastHandler { get; set; }

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

            // Enable CORS for Chrome extension & Web Gallery
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
                        version = "1.3.0",
                        active = true,
                        isBroadcasting = _isBroadcasting,
                        broadcastStreamId = _broadcastStreamId,
                        isWindowCasting = CaptureService?.IsCapturing ?? false,
                        windowCastSource = CaptureService?.CurrentSource?.Title
                    });
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/api/stream/status")
                {
                    await WriteJsonResponseAsync(res, 200, new
                    {
                        active = _isBroadcasting,
                        streamId = _broadcastStreamId,
                        title = _broadcastTitle,
                        faceIndex = _broadcastFaceIndex,
                        allFaces = _broadcastAllFaces
                    });
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/api/monitors")
                {
                    var monitors = GetMonitorsHandler?.Invoke() ?? Array.Empty<object>();
                    await WriteJsonResponseAsync(res, 200, new { monitors });
                    return;
                }

                #region Window Caster Endpoints

                // 1. Live MJPEG Stream for Wallpaper View & Web Gallery
                if (req.HttpMethod == "GET" && path == "/api/stream/window.mjpg")
                {
                    await HandleMjpegStreamAsync(context);
                    return;
                }

                // 2. Single JPEG Preview Snapshot
                if (req.HttpMethod == "GET" && path == "/api/stream/window/preview.jpg")
                {
                    var frame = CaptureService?.LatestJpegFrame;
                    if (frame != null && frame.Length > 0)
                    {
                        res.ContentType = "image/jpeg";
                        res.ContentLength64 = frame.Length;
                        await res.OutputStream.WriteAsync(frame, 0, frame.Length);
                        res.Close();
                    }
                    else
                    {
                        res.StatusCode = 204;
                        res.Close();
                    }
                    return;
                }

                // 3. Enumerate Available Windows / Screens
                if (req.HttpMethod == "GET" && path == "/api/windows")
                {
                    var windows = WindowCaptureService.GetAvailableCaptureSources().Select(w => new
                    {
                        hwnd = w.Hwnd.ToInt64(),
                        title = w.Title,
                        processName = w.ProcessName,
                        isScreen = w.IsScreen,
                        screenIndex = w.ScreenIndex,
                        bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height }
                    }).ToArray();

                    await WriteJsonResponseAsync(res, 200, new { windows });
                    return;
                }

                // 4. Trigger Window Caster UI to Show
                if (req.HttpMethod == "GET" && path == "/api/window-caster/show")
                {
                    ShowWindowCasterHandler?.Invoke();
                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                // 5. Start Window Cast via REST API
                if (req.HttpMethod == "POST" && path == "/api/stream/window/start")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    long hwndVal = root.TryGetProperty("hwnd", out var hProp) ? hProp.GetInt64() : 0;
                    int faceIndex = ParseIntProperty(root, "faceIndex", -1);
                    int monitorIndex = ParseIntProperty(root, "monitorIndex", -1);
                    bool allFaces = root.TryGetProperty("allFaces", out var afProp) ? afProp.GetBoolean() : (faceIndex == -1);
                    int fps = ParseIntProperty(root, "fps", 30);

                    var allSources = WindowCaptureService.GetAvailableCaptureSources();
                    var source = allSources.FirstOrDefault(s => s.Hwnd.ToInt64() == hwndVal) ?? allSources.FirstOrDefault();

                    if (source != null && CaptureService != null)
                    {
                        CaptureService.StartCapture(source, fps);

                        string streamUrl = $"http://127.0.0.1:{Port}/api/stream/window.mjpg?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                        if (StartWindowCastHandler != null)
                        {
                            await StartWindowCastHandler.Invoke(faceIndex, allFaces, streamUrl, monitorIndex);
                        }

                        await WriteJsonResponseAsync(res, 200, new
                        {
                            success = true,
                            windowTitle = source.Title,
                            faceIndex,
                            allFaces,
                            monitorIndex,
                            fps
                        });
                    }
                    else
                    {
                        await WriteJsonResponseAsync(res, 400, new { error = "Target window source not found." });
                    }
                    return;
                }

                // 6. Stop Window Cast via REST API
                if (req.HttpMethod == "POST" && path == "/api/stream/window/stop")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    int faceIndex = -1;
                    int monitorIndex = -1;
                    bool allFaces = true;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            using var doc = JsonDocument.Parse(body);
                            faceIndex = ParseIntProperty(doc.RootElement, "faceIndex", -1);
                            monitorIndex = ParseIntProperty(doc.RootElement, "monitorIndex", -1);
                            allFaces = doc.RootElement.TryGetProperty("allFaces", out var af) ? af.GetBoolean() : (faceIndex == -1);
                        }
                    }
                    catch { }

                    CaptureService?.StopCapture();
                    if (StopWindowCastHandler != null)
                    {
                        await StopWindowCastHandler.Invoke(faceIndex, allFaces, monitorIndex);
                    }

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                #endregion

                #region Chrome Extension WebRTC Endpoints

                if (req.HttpMethod == "POST" && path == "/api/stream/offer")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    int faceIndex = ParseIntProperty(root, "faceIndex", -1);
                    int monitorIndex = ParseIntProperty(root, "monitorIndex", -1);
                    string sdp = root.TryGetProperty("sdp", out var s) ? s.GetString() ?? "" : "";

                    AppLogger.Log($"LocalStreamBridge received WebRTC Offer for face {faceIndex} (all faces: {faceIndex == -1}), monitor {monitorIndex} ({sdp.Length} chars)");

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
                    int faceIndex = ParseIntProperty(root, "faceIndex", -1);
                    int monitorIndex = ParseIntProperty(root, "monitorIndex", -1);
                    var candidateJson = root.TryGetProperty("candidate", out var c) ? c.GetRawText() : "{}";

                    CandidateHandler?.Invoke(faceIndex, monitorIndex, candidateJson);

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/stop")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    int faceIndex = -1;
                    int monitorIndex = -1;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        faceIndex = ParseIntProperty(doc.RootElement, "faceIndex", -1);
                        monitorIndex = ParseIntProperty(doc.RootElement, "monitorIndex", -1);
                    }
                    catch { }

                    AppLogger.Log($"LocalStreamBridge received Chrome STOP for face {faceIndex}, monitor {monitorIndex}");
                    StopHandler?.Invoke(faceIndex, monitorIndex);

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/broadcast/start")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    string streamId = root.TryGetProperty("streamId", out var sProp) ? sProp.GetString() ?? "" : $"stream_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    string title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Chrome Video Sync" : "Chrome Video Sync";
                    int faceIndex = ParseIntProperty(root, "faceIndex", -1);
                    bool allFaces = !root.TryGetProperty("allFaces", out var afProp) || afProp.GetBoolean();

                    lock (_broadcastLock)
                    {
                        _isBroadcasting = true;
                        _broadcastStreamId = streamId;
                        _broadcastTitle = title;
                        _broadcastFaceIndex = faceIndex;
                        _broadcastAllFaces = allFaces;
                        _localSignalQueues.Clear();
                    }

                    AppLogger.Log($"LocalStreamBridge: Network broadcast started (id={streamId}, faces={(allFaces ? "all" : faceIndex.ToString())}, title='{title}')");

                    // Notify central Rails API if available
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var payload = new
                            {
                                active = true,
                                stream_id = streamId,
                                title = title,
                                face_index = faceIndex,
                                all_faces = allFaces,
                                lan_bridge_url = $"http://127.0.0.1:{Port}"
                            };
                            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                            await _httpClient.PostAsync("http://162.35.101.183:3000/api/v1/stream/cast", content);
                        }
                        catch { }
                    });

                    await WriteJsonResponseAsync(res, 200, new { success = true, streamId });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/broadcast/stop")
                {
                    lock (_broadcastLock)
                    {
                        _isBroadcasting = false;
                        _broadcastStreamId = null;
                        _broadcastTitle = null;
                        _localSignalQueues.Clear();
                    }

                    AppLogger.Log("LocalStreamBridge: Network broadcast stopped.");
                    StopHandler?.Invoke(-1, -1);

                    // Notify central Rails API
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var payload = new { active = false };
                            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                            await _httpClient.PostAsync("http://162.35.101.183:3000/api/v1/stream/cast", content);
                        }
                        catch { }
                    });

                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/api/stream/signal")
                {
                    using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    string target = root.TryGetProperty("target", out var tp) ? tp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var queue = _localSignalQueues.GetOrAdd(target, _ => new ConcurrentQueue<string>());
                        queue.Enqueue(body);
                    }
                    await WriteJsonResponseAsync(res, 200, new { success = true });
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/api/stream/signals")
                {
                    string receiverId = req.QueryString["receiver_id"] ?? "";
                    var list = new List<object>();
                    if (!string.IsNullOrWhiteSpace(receiverId) && _localSignalQueues.TryGetValue(receiverId, out var queue))
                    {
                        while (queue.TryDequeue(out var raw))
                        {
                            try { list.Add(JsonSerializer.Deserialize<JsonElement>(raw)); } catch { }
                        }
                    }
                    await WriteJsonResponseAsync(res, 200, new { signals = list });
                    return;
                }

                #endregion

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

        private async Task HandleMjpegStreamAsync(HttpListenerContext context)
        {
            var res = context.Response;
            res.ContentType = "multipart/x-mixed-replace; boundary=--frame";
            res.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            res.Headers.Add("Pragma", "no-cache");
            res.Headers.Add("Expires", "0");
            res.SendChunked = true;

            var outputStream = res.OutputStream;
            var frameSignal = new SemaphoreSlim(0, 10);
            byte[]? pendingFrame = null;

            Action<byte[]> onFrame = (frame) =>
            {
                pendingFrame = frame;
                if (frameSignal.CurrentCount < 2)
                {
                    try { frameSignal.Release(); } catch { }
                }
            };

            if (CaptureService != null)
            {
                CaptureService.FrameCaptured += onFrame;
                if (CaptureService.LatestJpegFrame != null)
                {
                    onFrame(CaptureService.LatestJpegFrame);
                }
            }

            try
            {
                while (_isRunning && (_cts == null || !_cts.IsCancellationRequested))
                {
                    bool signaled = await frameSignal.WaitAsync(1000);
                    byte[]? frameToSend = pendingFrame;

                    if (frameToSend != null && frameToSend.Length > 0)
                    {
                        string header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frameToSend.Length}\r\n\r\n";
                        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                        byte[] footerBytes = Encoding.ASCII.GetBytes("\r\n");

                        await outputStream.WriteAsync(headerBytes, 0, headerBytes.Length);
                        await outputStream.WriteAsync(frameToSend, 0, frameToSend.Length);
                        await outputStream.WriteAsync(footerBytes, 0, footerBytes.Length);
                        await outputStream.FlushAsync();
                    }
                }
            }
            catch
            {
                // Client disconnected cleanly
            }
            finally
            {
                if (CaptureService != null)
                {
                    CaptureService.FrameCaptured -= onFrame;
                }
                try { res.Close(); } catch { }
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

        private static int ParseIntProperty(JsonElement element, string propName, int defaultValue)
        {
            if (element.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int num))
                {
                    return num;
                }
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var str = prop.GetString();
                    if (int.TryParse(str, out int parsed)) return parsed;
                    if (string.Equals(str, "all", StringComparison.OrdinalIgnoreCase)) return -1;
                }
            }
            return defaultValue;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
