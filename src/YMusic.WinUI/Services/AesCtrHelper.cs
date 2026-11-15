#nullable enable

using System;
using System.Security.Cryptography;

namespace YMusic.WinUI.Services;

/// <summary>
/// Helper for AES-128-CTR stream decryption used by Yandex Music FLAC-in-MP4 streams.
/// Nonce is 16 bytes of zeros; counter is big-endian block index.
/// </summary>
public static class AesCtrHelper
{
    public static void DecryptBlock(byte[] key, long streamOffset, byte[] input, int inputOffset, int length, byte[] output, int outputOffset)
    {
        if (length <= 0) return;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        long blockIndex = streamOffset / 16;
        int blockOffset = (int)(streamOffset % 16);

        byte[] counterBlock = new byte[16];
        byte[] keyStream = new byte[16];

        int bytesProcessed = 0;
        while (bytesProcessed < length)
        {
            Array.Clear(counterBlock, 0, 16);
            long temp = blockIndex;
            for (int i = 15; i >= 8; i--)
            {
                counterBlock[i] = (byte)(temp & 0xFF);
                temp >>= 8;
            }

            encryptor.TransformBlock(counterBlock, 0, 16, keyStream, 0);

            int chunkLen = Math.Min(length - bytesProcessed, 16 - blockOffset);
            for (int i = 0; i < chunkLen; i++)
            {
                output[outputOffset + bytesProcessed + i] = (byte)(input[inputOffset + bytesProcessed + i] ^ keyStream[blockOffset + i]);
            }

            bytesProcessed += chunkLen;
            blockIndex++;
            blockOffset = 0;
        }
    }
}
