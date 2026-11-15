#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YMusic.WinUI.Services.Interfaces;

public sealed class ListeningStatsSummary
{
    public int TotalTracksPlayed { get; set; }
    public long TotalListeningSeconds { get; set; }

    public int TodayTracksPlayed { get; set; }
    public long TodayListeningSeconds { get; set; }

    public int WeekTracksPlayed { get; set; }
    public long WeekListeningSeconds { get; set; }
}

public sealed class DailyListeningData
{
    public string DateKey { get; set; } = string.Empty; // "yyyy-MM-dd"
    public string DayLabel { get; set; } = string.Empty; // "Пн", "Вт", etc.
    public int TracksCount { get; set; }
    public long ListeningSeconds { get; set; }
    public double BarHeight { get; set; } // Scaled 12..80 for UI bar chart
    public string TooltipText { get; set; } = string.Empty;
}

public interface IPlaybackStatisticsService : IDisposable
{
    void RecordProgress(string trackId, TimeSpan position, TimeSpan duration, bool isPlaying);
    void TrackPlaybackFinished(string trackId);
    ListeningStatsSummary GetSummary();
    IReadOnlyList<DailyListeningData> GetLastDays(int count = 7);
    Task ClearStatisticsAsync();
    event EventHandler? StatisticsUpdated;
}
