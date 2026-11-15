#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using YMusic.WinUI.Services.Interfaces;

namespace YMusic.WinUI.Services.Implementations;

public sealed class FlacStreamProxy : IFlacStreamProxy
{
    private sealed class StreamMetadata : IDisposable
    {
        public string CdnUrl { get; }
        public DateTime CreatedAt { get; }

        private byte[]? _key;
        private int _refCount = 1;
        private bool _isDisposed;
        private readonly object _lock = new();

        public StreamMetadata(string cdnUrl, byte[] key)
        {
            CdnUrl = cdnUrl;
            _key = (byte[])key.Clone();
            CreatedAt = DateTime.UtcNow;
        }

        public bool TryAddRef()
        {
            lock (_lock)
            {
                if (_isDisposed || _key == null) return false;
                _refCount++;
                return true;
            }
        }

        public byte[]? GetKeyCopy()
        {
            lock (_lock)
            {
                if (_isDisposed || _key == null) return null;
                return (byte[])_key.Clone();
            }
        }

        public void Release()
        {
            byte[]? toZero = null;
            lock (_lock)
            {
                _refCount--;
                if (_refCount <= 0 && !_isDisposed)
                {
                    _isDisposed = true;
                    toZero = _key;
                    _key = null;
                }
            }

            if (toZero != null)
            {
                try
                {
                    CryptographicOperations.ZeroMemory(toZero);
                }
                catch { }
            }
        }

        public void Dispose() => Release();
    }

    private readonly HttpListener _listener;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, StreamMetadata> _activeStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private bool _disposed;

    public int Port => _port;

    public FlacStreamProxy()
    {
        _httpClient = new HttpClient(YandexBypassProxy.CreateHandler(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15)))
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        int port = 0;
        HttpListener? listener = null;
        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                tcpListener.Start();
                port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
                tcpListener.Stop();

                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/flac/");
                listener.Start();
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlacStreamProxy] Port {port} binding failed, retrying: {ex.Message}");
                try { listener?.Close(); } catch { }
                listener = null;
            }
        }

        _port = port;
        _listener = listener ?? new HttpListener();
        if (!_listener.IsListening)
        {
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/flac/");
                _listener.Start();
            }
            catch { }
        }

        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public string RegisterStream(string trackId, string cdnUrl, byte[] keyBytes)
    {
        // Prune stale stream metadata to prevent memory leak
        if (_activeStreams.Count > 40)
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kvp in _activeStreams)
            {
                if (kvp.Value.CreatedAt < cutoff || _activeStreams.Count > 50)
                {
                    if (_activeStreams.TryRemove(kvp.Key, out var oldMeta))
                    {
                        oldMeta.Release();
                    }
                }
            }
        }

        var newMeta = new StreamMetadata(cdnUrl, keyBytes);
        if (_activeStreams.TryGetValue(trackId, out var existing))
        {
            _activeStreams[trackId] = newMeta;
            existing.Release();
        }
        else
        {
            _activeStreams[trackId] = newMeta;
        }

        return $"http://127.0.0.1:{_port}/flac/{Uri.EscapeDataString(trackId)}";
    }

    public void UnregisterStream(string trackId)
    {
        if (_activeStreams.TryRemove(trackId, out var meta))
        {
            meta.Release();
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlacStreamProxy] Error in ListenLoop: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        StreamMetadata? meta = null;
        byte[]? keyCopy = null;

        try
        {
            if (!context.Request.IsLocal)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                response.Close();
                return;
            }

            string rawPath = context.Request.Url?.AbsolutePath ?? string.Empty;
            const string prefix = "/flac/";
            if (!rawPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
                return;
            }

            string encodedTrackId = rawPath.Substring(prefix.Length);
            string trackId = Uri.UnescapeDataString(encodedTrackId).Trim();

            if (!_activeStreams.TryGetValue(trackId, out meta) || !meta.TryAddRef())
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            keyCopy = meta.GetKeyCopy();
            if (keyCopy == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, meta.CdnUrl);

            long startOffset = 0;
            string? rangeHeader = context.Request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                if (RangeHeaderValue.TryParse(rangeHeader, out var parsedRange))
                {
                    upstreamReq.Headers.Range = parsedRange;
                    var firstRange = parsedRange.Ranges.Count > 0 ? parsedRange.Ranges.GetEnumerator() : null;
                    if (firstRange != null && firstRange.MoveNext() && firstRange.Current.From.HasValue)
                    {
                        startOffset = firstRange.Current.From.Value;
                    }
                }
            }

            using var upstreamResp = await _httpClient.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            response.StatusCode = (int)upstreamResp.StatusCode;
            response.ContentType = "audio/mp4";
            response.Headers["Accept-Ranges"] = "bytes";

            if (upstreamResp.Content.Headers.ContentRange != null)
            {
                response.Headers["Content-Range"] = upstreamResp.Content.Headers.ContentRange.ToString();
            }

            if (upstreamResp.Content.Headers.ContentLength.HasValue)
            {
                response.ContentLength64 = upstreamResp.Content.Headers.ContentLength.Value;
            }

            if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                response.Close();
                return;
            }

            using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var clientStream = response.OutputStream;

            // Single decryptor instance per stream request
            using var decryptor = new AesCtrDecryptor(keyCopy);

            byte[] buffer = new byte[65536];
            byte[] decrypted = new byte[65536];
            long currentOffset = startOffset;

            int read;
            while ((read = await upstreamStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                decryptor.DecryptBlock(currentOffset, buffer, 0, read, decrypted, 0);
                await clientStream.WriteAsync(decrypted, 0, read, ct).ConfigureAwait(false);
                currentOffset += read;
            }

            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException)
        {
            // Client closed connection or sought to another position - expected in media streaming
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FlacStreamProxy] Error serving stream: {ex.Message}");
        }
        finally
        {
            if (keyCopy != null)
            {
                try { CryptographicOperations.ZeroMemory(keyCopy); } catch { }
            }
            meta?.Release();
            try { response.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _httpClient.Dispose();
            _cts.Dispose();

            foreach (var meta in _activeStreams.Values)
            {
                meta.Release();
            }
            _activeStreams.Clear();
        }
        catch { }
    }
}
