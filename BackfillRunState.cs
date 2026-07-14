namespace QzoneLikeAssistant;

internal sealed class BackfillRunState
{
    public int ScrollsRemaining { get; private set; }
    public int Attempts { get; private set; }
    public int Successes { get; private set; }
    public bool PassPending { get; private set; }
    public bool PassAtTop { get; private set; }
    public bool TopComplete { get; private set; } = true;
    public DateTime DeadlineUtc { get; private set; } = DateTime.MinValue;
    public string SessionId { get; private set; } = "";

    public void Start(bool enabled, int scrollLimit, DateTime nowUtc, TimeSpan duration)
    {
        ScrollsRemaining = enabled ? Math.Max(0, scrollLimit) : 0;
        Attempts = 0;
        Successes = 0;
        DeadlineUtc = nowUtc + duration;
        SessionId = Guid.NewGuid().ToString("N");
        ResetPageContext(enabled);
    }

    public void ResetPageContext(bool enabled)
    {
        PassPending = false;
        PassAtTop = false;
        TopComplete = !enabled || ScrollsRemaining <= 0;
    }

    public void CancelPass()
    {
        PassPending = false;
        PassAtTop = false;
    }

    public void BeginTopPass()
    {
        PassAtTop = true;
        PassPending = true;
    }

    public void BeginScrolledPass()
    {
        PassAtTop = false;
        PassPending = true;
        if (ScrollsRemaining > 0) ScrollsRemaining -= 1;
    }

    public void MarkTopComplete() => TopComplete = true;

    public void RegisterAttempt() => Attempts += 1;

    public void RegisterSuccess() => Successes += 1;

    public bool CanContinue(bool enabled, int budget, DateTime nowUtc) =>
        enabled && ScrollsRemaining > 0 && Attempts < Math.Max(0, budget) && nowUtc < DeadlineUtc;

    public bool CanExecutePending(bool enabled, int budget, DateTime nowUtc) =>
        enabled && PassPending && Attempts < Math.Max(0, budget) && nowUtc < DeadlineUtc;

    public void Finish()
    {
        ScrollsRemaining = 0;
        CancelPass();
    }
}
