#nullable enable

using System;

namespace YMusic.WinUI.Services.Interfaces;

public interface IFlacStreamProxy : IDisposable
{
    string RegisterStream(string trackId, string cdnUrl, byte[] keyBytes);
    void UnregisterStream(string trackId);
}
