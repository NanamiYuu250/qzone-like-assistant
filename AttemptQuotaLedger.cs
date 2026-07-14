namespace QzoneLikeAssistant;

internal sealed class AttemptQuotaLedger(int capacity = 4096)
{
    private readonly BoundedKeySet registeredAttemptIds = new(capacity);

    public bool Register(string attemptId, AppSettings settings, DateTime? attemptedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(attemptId) || !registeredAttemptIds.Add(attemptId)) return false;
        settings.ResetStatsIfNeeded();
        settings.TodayAttemptCount += 1;
        settings.RecordActionAt(attemptedAtUtc ?? DateTime.UtcNow);
        return true;
    }
}
