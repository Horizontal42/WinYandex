#nullable enable

namespace YMusic.WinUI.Models;

public enum AudioQualityPreference
{
    Lossless,
    AacPreferred,
    Mp3Preferred,
    HighestBitrate
}

public sealed class StreamAudioQuality
{
    public string Codec { get; set; } = string.Empty;
    public int BitrateInKbps { get; set; }
    public bool IsCached { get; set; }

    public string DisplayText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Codec)) return string.Empty;
            if (Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase))
            {
                return $"FLAC • Lossless{(IsCached ? " (Cached)" : string.Empty)}";
            }
            return $"{Codec.ToUpperInvariant()} • {BitrateInKbps} kbps{(IsCached ? " (Cached)" : string.Empty)}";
        }
    }

    public string BadgeText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Codec)) return "HQ";
            if (Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase)) return "FLAC";
            if (Codec.Equals("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (BitrateInKbps >= 320) return "320k";
            return $"{BitrateInKbps}k";
        }
    }
}
