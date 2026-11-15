#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using YMusic.WinUI.Services.Interfaces;

namespace YMusic.WinUI.Services.Implementations;

public sealed class DayStatsRecord
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("tracksCount")]
    public int TracksCount { get; set; }

    [JsonPropertyName("listeningSeconds")]
    public long ListeningSeconds { get; set; }
}

public sealed class PlaybackStatisticsStorage
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("totalTracksPlayed")]
    public int TotalTracksPlayed { get; set; }

    [JsonPropertyName("totalListeningSeconds")]
    public long TotalListeningSeconds { get; set; }

    [JsonPropertyName("dailyStats")]
    public Dictionary<string, DayStatsRecord> DailyStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlaybackStatisticsService : IPlaybackStatisticsService
{
    private readonly string _storageFilePath;
    private readonly object _lock = new();
    private PlaybackStatisticsStorage _storage = new();
    private readonly System.Timers.Timer _debounceSaveTimer;
    private bool _isDirty;
    private bool _disposed;

    // Session tracking to prevent duplicate counting
    private string? _currentActiveTrackId;
    private string _currentSessionId = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _creditedTracksInSession = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastProgressTimestamp = DateTime.UtcNow;
    private double _accumulatedSeconds;

    public event EventHandler? StatisticsUpdated;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public PlaybackStatisticsService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dataDir = Path.Combine(appData, "YMusic", "Data");
        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch { }

        _storageFilePath = Path.Combine(dataDir, "playback_statistics.json");

        _debounceSaveTimer = new System.Timers.Timer(3000) { AutoReset = false };
        _debounceSaveTimer.Elapsed += (_, _) => FlushIfDirty();

        LoadStorage();
    }

    private void LoadStorage()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_storageFilePath))
                {
                    string json = File.ReadAllText(_storageFilePath);
                    var loaded = JsonSerializer.Deserialize<PlaybackStatisticsStorage>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _storage = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Stats] Failed to load statistics: {ex.Message}");
            }
        }
    }

    public void RecordProgress(string trackId, TimeSpan position, TimeSpan duration, bool isPlaying)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !isPlaying)
        {
            _lastProgressTimestamp = DateTime.UtcNow;
            return;
        }

        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = (now - _lastProgressTimestamp).TotalSeconds;
        _lastProgressTimestamp = now;

        // Ignore absurd deltas (e.g. computer resume from sleep or seek)
        if (elapsedSeconds < 0.05 || elapsedSeconds > 4.0)
        {
            elapsedSeconds = 0.25;
        }

        string todayKey = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool shouldNotify = false;

        lock (_lock)
        {
            if (!_storage.DailyStats.TryGetValue(todayKey, out var todayRecord))
            {
                todayRecord = new DayStatsRecord { Date = todayKey };
                _storage.DailyStats[todayKey] = todayRecord;
            }

            _accumulatedSeconds += elapsedSeconds;
            long addedSeconds = (long)_accumulatedSeconds;
            if (addedSeconds > 0)
            {
                _accumulatedSeconds -= addedSeconds;
                todayRecord.ListeningSeconds += addedSeconds;
                _storage.TotalListeningSeconds += addedSeconds;
                _isDirty = true;
            }

            // Track switch detection
            if (!string.Equals(_currentActiveTrackId, trackId, StringComparison.OrdinalIgnoreCase))
            {
                _currentActiveTrackId = trackId;
                _currentSessionId = Guid.NewGuid().ToString("N");
            }

            // Check qualification for played track count (>= 30 seconds OR >= 50% of track)
            bool isQualified = position.TotalSeconds >= 30.0 ||
                               (duration > TimeSpan.Zero && position.TotalSeconds >= duration.TotalSeconds * 0.5);

            string sessionKey = $"{trackId}_{_currentSessionId}";
            if (isQualified && !_creditedTracksInSession.Contains(sessionKey))
            {
                _creditedTracksInSession.Add(sessionKey);
                todayRecord.TracksCount++;
                _storage.TotalTracksPlayed++;
                _isDirty = true;
                shouldNotify = true;
            }

            if (_creditedTracksInSession.Count > 2000)
            {
                _creditedTracksInSession.Clear();
            }

            if (_isDirty)
            {
                _debounceSaveTimer.Stop();
                _debounceSaveTimer.Start();
            }
        }

        if (shouldNotify)
        {
            StatisticsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void TrackPlaybackFinished(string trackId)
    {
        lock (_lock)
        {
            _currentActiveTrackId = null;
            _currentSessionId = Guid.NewGuid().ToString("N");
        }
    }

    public ListeningStatsSummary GetSummary()
    {
        lock (_lock)
        {
            DateTime now = DateTime.Now;
            string todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            int todayTracks = 0;
            long todaySecs = 0;
            if (_storage.DailyStats.TryGetValue(todayKey, out var todayRec))
            {
                todayTracks = todayRec.TracksCount;
                todaySecs = todayRec.ListeningSeconds;
            }

            int weekTracks = 0;
            long weekSecs = 0;
            for (int i = 0; i < 7; i++)
            {
                string key = now.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_storage.DailyStats.TryGetValue(key, out var r))
                {
                    weekTracks += r.TracksCount;
                    weekSecs += r.ListeningSeconds;
                }
            }

            return new ListeningStatsSummary
            {
                TotalTracksPlayed = _storage.TotalTracksPlayed,
                TotalListeningSeconds = _storage.TotalListeningSeconds,
                TodayTracksPlayed = todayTracks,
                TodayListeningSeconds = todaySecs,
                WeekTracksPlayed = weekTracks,
                WeekListeningSeconds = weekSecs
            };
        }
    }

    public IReadOnlyList<DailyListeningData> GetLastDays(int count = 7)
    {
        var result = new List<DailyListeningData>(count);
        DateTime today = DateTime.Today;

        lock (_lock)
        {
            long maxSeconds = 1;
            var tempDays = new List<(string DateKey, string Label, int Tracks, long Secs)>();

            for (int i = count - 1; i >= 0; i--)
            {
                DateTime day = today.AddDays(-i);
                string dateKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                string dayLabel = i == 0 ? (App.Loc?.CurrentLanguage == "en" ? "Today" : "Сегодня")
                                : day.ToString("ddd", CultureInfo.GetCultureInfo(App.Loc?.CurrentLanguage == "en" ? "en-US" : "ru-RU"));

                int tracks = 0;
                long secs = 0;
                if (_storage.DailyStats.TryGetValue(dateKey, out var rec))
                {
                    tracks = rec.TracksCount;
                    secs = rec.ListeningSeconds;
                }

                if (secs > maxSeconds) maxSeconds = secs;
                tempDays.Add((dateKey, dayLabel, tracks, secs));
            }

            foreach (var (dateKey, label, tracks, secs) in tempDays)
            {
                // Bar height from 10 to 65 px
                double ratio = (double)secs / maxSeconds;
                double height = secs > 0 ? Math.Max(10.0, ratio * 65.0) : 4.0;

                string hoursStr;
                var ts = TimeSpan.FromSeconds(secs);
                if (ts.TotalHours >= 1)
                {
                    hoursStr = $"{Math.Floor(ts.TotalHours)}ч {ts.Minutes}мин";
                }
                else
                {
                    hoursStr = $"{ts.Minutes} мин";
                }

                string tooltip = $"{label} ({dateKey}): {tracks} треков • {hoursStr}";

                result.Add(new DailyListeningData
                {
                    DateKey = dateKey,
                    DayLabel = label,
                    TracksCount = tracks,
                    ListeningSeconds = secs,
                    BarHeight = height,
                    TooltipText = tooltip
                });
            }
        }

        return result;
    }

    public Task ClearStatisticsAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                _debounceSaveTimer.Stop();
                _storage = new PlaybackStatisticsStorage();
                _isDirty = false;
                try
                {
                    if (File.Exists(_storageFilePath))
                    {
                        File.Delete(_storageFilePath);
                    }
                }
                catch { }
            }
            StatisticsUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    private void FlushIfDirty()
    {
        lock (_lock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            try
            {
                string json = JsonSerializer.Serialize(_storage, JsonOptions);
                string tempPath = _storageFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storageFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Stats] Error saving stats: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceSaveTimer.Stop();
        _debounceSaveTimer.Dispose();
        FlushIfDirty();
    }
}
