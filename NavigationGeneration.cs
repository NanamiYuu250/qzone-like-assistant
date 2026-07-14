namespace QzoneLikeAssistant;

internal sealed class NavigationGeneration
{
    public ulong CurrentId { get; private set; }
    public bool InProgress { get; private set; }

    public void MarkPending()
    {
        CurrentId = 0;
        InProgress = true;
    }

    public void Start(ulong navigationId)
    {
        CurrentId = navigationId;
        InProgress = true;
    }

    public bool TryComplete(ulong navigationId)
    {
        if (!InProgress || CurrentId != navigationId) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        CurrentId = 0;
        InProgress = false;
    }
}
