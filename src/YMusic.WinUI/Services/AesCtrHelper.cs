#nullable enable

using System;
using System.Security.Cryptography;

namespace YMusic.WinUI.Services;

/// <summary>
/// High-performance thread-safe AES-128-CTR stream decryptor for Yandex Music FLAC-in-MP4 streams.
/// Reuses Aes and ICryptoTransform instances across the lifetime of an HTTP connection.
/// Nonce is 16 bytes of zeros; counter is big-endian block index.
/// </summary>
public sealed class AesCtrDecryptor : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _counterBlock = new byte[16];
    private readonly byte[] _keyStream = new byte[16];
    private bool _disposed;

    public AesCtrDecryptor(byte[] key)
    {
        if (key == null || key.Length != 16)
        {
            throw new ArgumentException("AES-128 key must be exactly 16 bytes", nameof(key));
        }

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        _encryptor = _aes.CreateEncryptor();
    }

    public void DecryptBlock(long streamOffset, byte[] input, int inputOffset, int length, byte[] output, int outputOffset)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AesCtrDecryptor));
        if (length <= 0) return;

        long blockIndex = streamOffset / 16;
        int blockOffset = (int)(streamOffset % 16);

        int bytesProcessed = 0;
        while (bytesProcessed < length)
        {
            Array.Clear(_counterBlock, 0, 16);
            long temp = blockIndex;
            for (int i = 15; i >= 8; i--)
            {
                _counterBlock[i] = (byte)(temp & 0xFF);
                temp >>= 8;
            }

            _encryptor.TransformBlock(_counterBlock, 0, 16, _keyStream, 0);

            int chunkLen = Math.Min(length - bytesProcessed, 16 - blockOffset);
            for (int i = 0; i < chunkLen; i++)
            {
                output[outputOffset + bytesProcessed + i] = (byte)(input[inputOffset + bytesProcessed + i] ^ _keyStream[blockOffset + i]);
            }

            bytesProcessed += chunkLen;
            blockIndex++;
            blockOffset = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encryptor.Dispose();
        _aes.Dispose();
        Array.Clear(_counterBlock, 0, _counterBlock.Length);
        Array.Clear(_keyStream, 0, _keyStream.Length);
    }
}

public static class AesCtrHelper
{
    public static void DecryptBlock(byte[] key, long streamOffset, byte[] input, int inputOffset, int length, byte[] output, int outputOffset)
    {
        using var decryptor = new AesCtrDecryptor(key);
        decryptor.DecryptBlock(streamOffset, input, inputOffset, length, output, outputOffset);
    }
}
