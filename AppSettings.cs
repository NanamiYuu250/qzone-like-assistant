using System.Text.Json;
using System.IO;

namespace QzoneLikeAssistant;

internal sealed class AppSettings
{
    private const int CurrentSettingsVersion = 6;

    public int SettingsVersion { get; set; }
    public int ScanSeconds { get; set; } = 1;
    public int MinActionSeconds { get; set; } = 3;
    public int DailyLimit { get; set; } = 300;
    public bool AutoRefreshEnabled { get; set; } = true;
    public int AutoRefreshMinutes { get; set; } = 5;
    public bool BackfillOnStart { get; set; } = true;
    public int BackfillLikeLimit { get; set; } = 10;
    public string IncludeKeywords { get; set; } = "";
    public string ExcludeKeywords { get; set; } = "广告,抽奖,代购";
    public string StatsDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int TodayCount { get; set; }
    public int TodayAttemptCount { get; set; }
    public DateTime LastActionAtUtc { get; set; } = DateTime.MinValue;

    public void Normalize()
    {
        var sourceVersion = SettingsVersion;
        if (sourceVersion < 5)
        {
            if (ScanSeconds == 2) ScanSeconds = 1;
            if (MinActionSeconds == 8) MinActionSeconds = 3;
            if (DailyLimit == 30) DailyLimit = 300;
        }
        if (sourceVersion < CurrentSettingsVersion) SettingsVersion = CurrentSettingsVersion;

        ScanSeconds = Math.Clamp(ScanSeconds, 1, 60);
        MinActionSeconds = Math.Clamp(MinActionSeconds, 1, 600);
        DailyLimit = Math.Clamp(DailyLimit, 1, 5000);
        AutoRefreshMinutes = Math.Clamp(AutoRefreshMinutes, 1, 60);
        BackfillLikeLimit = Math.Clamp(BackfillLikeLimit, 1, 100);
        TodayAttemptCount = Math.Max(TodayAttemptCount, TodayCount);
        if (LastActionAtUtc != DateTime.MinValue)
        {
            LastActionAtUtc = LastActionAtUtc.Kind switch
            {
                DateTimeKind.Utc => LastActionAtUtc,
                DateTimeKind.Local => LastActionAtUtc.ToUniversalTime(),
                _ => DateTime.SpecifyKind(LastActionAtUtc, DateTimeKind.Utc)
            };
            if (LastActionAtUtc > DateTime.UtcNow) LastActionAtUtc = DateTime.UtcNow;
        }
        IncludeKeywords = (IncludeKeywords ?? "").Trim()[..Math.Min((IncludeKeywords ?? "").Trim().Length, 200)];
        ExcludeKeywords = (ExcludeKeywords ?? "").Trim()[..Math.Min((ExcludeKeywords ?? "").Trim().Length, 200)];
        ResetStatsIfNeeded();
    }

    public bool IsActionCooldownElapsed(DateTime utcNow)
    {
        if (LastActionAtUtc == DateTime.MinValue) return true;
        if (utcNow.Kind != DateTimeKind.Utc) utcNow = utcNow.ToUniversalTime();
        return utcNow - LastActionAtUtc >= TimeSpan.FromSeconds(MinActionSeconds);
    }

    public void RecordActionAt(DateTime utcNow)
    {
        LastActionAtUtc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
    }

    public void ResetStatsIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (StatsDate == today) return;
        StatsDate = today;
        TodayCount = 0;
        TodayAttemptCount = 0;
    }

    public static AppSettings Load(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            loaded.Normalize();
            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, true);
    }
}
