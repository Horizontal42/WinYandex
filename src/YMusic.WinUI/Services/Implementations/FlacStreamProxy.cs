#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using YMusic.WinUI.Services.Interfaces;

namespace YMusic.WinUI.Services.Implementations;

public sealed class FlacStreamProxy : IFlacStreamProxy
{
    private sealed record StreamMetadata(string CdnUrl, byte[] Key, DateTime CreatedAt);

    private readonly HttpListener _listener;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, StreamMetadata> _activeStreams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private bool _disposed;

    public int Port => _port;

    public FlacStreamProxy()
    {
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None
        });

        _listener = new HttpListener();

        // Pick a free local port
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        _port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/flac/");
        _listener.Start();

        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public string RegisterStream(string trackId, string cdnUrl, byte[] keyBytes)
    {
        _activeStreams[trackId] = new StreamMetadata(cdnUrl, keyBytes, DateTime.UtcNow);
        return $"http://127.0.0.1:{_port}/flac/{trackId}";
    }

    public void UnregisterStream(string trackId)
    {
        _activeStreams.TryRemove(trackId, out _);
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
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            // Expected path: /flac/{trackId}
            string trackId = path.TrimStart('/').Replace("flac/", "");

            if (!_activeStreams.TryGetValue(trackId, out var meta))
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

            byte[] buffer = new byte[65536];
            byte[] decrypted = new byte[65536];
            long currentOffset = startOffset;

            int read;
            while ((read = await upstreamStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                AesCtrHelper.DecryptBlock(meta.Key, currentOffset, buffer, 0, read, decrypted, 0);
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
        }
        catch { }
    }
}
