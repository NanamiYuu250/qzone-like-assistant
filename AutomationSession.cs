namespace QzoneLikeAssistant;

internal readonly record struct AutomationRun(long Id, string Token)
{
    public bool IsValid => Id > 0 && !string.IsNullOrWhiteSpace(Token);
}

internal sealed class AutomationSession
{
    private long nextId;
    private AutomationRun current;

    public AutomationRun Current => current;

    public AutomationRun Start()
    {
        current = new AutomationRun(++nextId, Guid.NewGuid().ToString("N"));
        return current;
    }

    public string Stop()
    {
        var stoppedToken = current.Token;
        current = default;
        nextId += 1;
        return stoppedToken ?? "";
    }

    public bool IsActive(AutomationRun run) => run.IsValid && run == current;
}
