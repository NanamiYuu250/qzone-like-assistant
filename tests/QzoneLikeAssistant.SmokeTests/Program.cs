using System.Diagnostics;
using System.Text.Json;
using QzoneLikeAssistant;

var settings = new AppSettings();
settings.Normalize();
Require(settings.ScanSeconds == 1, "默认扫描间隔应为 1 秒");
Require(settings.MinActionSeconds == 3, "默认点赞冷却应为 3 秒");
Require(settings.DailyLimit == 300, "默认每日上限应为 300 次");
Require(settings.AutoRefreshEnabled, "默认应开启安全自动刷新");
Require(settings.AutoRefreshMinutes == 5, "默认自动刷新间隔应为 5 分钟");
Require(settings.TodayAttemptCount == 0, "每日尝试数默认应为 0");
var version5Settings = new AppSettings
{
    SettingsVersion = 5,
    ScanSeconds = 2,
    MinActionSeconds = 8,
    DailyLimit = 30
};
version5Settings.Normalize();
Require(version5Settings.SettingsVersion == 6, "V5 设置应只提升版本号");
Require(version5Settings.ScanSeconds == 2 &&
        version5Settings.MinActionSeconds == 8 &&
        version5Settings.DailyLimit == 30,
    "V5 用户自定义扫描、冷却和每日上限不得被旧迁移覆盖");
settings.TodayCount = 3;
settings.Normalize();
Require(settings.TodayAttemptCount == 3, "旧设置迁移后尝试数不得低于确认成功数");

var bounded = new BoundedKeySet(1000);
for (var index = 0; index <= 1000; index++) bounded.Add($"key-{index}");
Require(bounded.Count == 1000, "去重集合必须保持容量上限");
Require(!bounded.Contains("key-0"), "FIFO 必须淘汰最早加入的键");
Require(bounded.Contains("key-1000"), "FIFO 不得淘汰刚加入的键");
bounded.Add("key-1000");
Require(bounded.Count == 1000, "重复键不得改变容量");

var session = new AutomationSession();
var firstRun = session.Start();
Require(session.IsActive(firstRun), "启动后的运行句柄必须有效");
var stoppedToken = session.Stop();
Require(stoppedToken == firstRun.Token, "停止时必须返回待失效的页面令牌");
Require(!session.IsActive(firstRun), "停止后旧运行句柄必须立即失效");
var secondRun = session.Start();
Require(session.IsActive(secondRun), "重新启动后的运行句柄必须有效");
Require(!session.IsActive(firstRun), "重新启动不得重新激活旧运行句柄");
session.Stop();
var raceSettings = new AppSettings();
var quotaLedger = new AttemptQuotaLedger();
Require(quotaLedger.Register("stopped-race-attempt", raceSettings),
    "同步返回的 attempt 在会话停止后仍必须登记额度");
Require(raceSettings.TodayAttemptCount == 1, "停止竞态中的真实 attempt 不得漏记");
Require(!quotaLedger.Register("stopped-race-attempt", raceSettings) && raceSettings.TodayAttemptCount == 1,
    "同一 attemptId 不得重复计费");

var cooldownAt = DateTime.UtcNow.AddMinutes(-1);
var cooldownSettings = new AppSettings { MinActionSeconds = 3 };
var cooldownLedger = new AttemptQuotaLedger();
Require(cooldownLedger.Register("cooldown-attempt", cooldownSettings, cooldownAt),
    "真实 attempt 应记录冷却时间");
var restartSession = new AutomationSession();
restartSession.Start();
restartSession.Stop();

var backfill = new BackfillRunState();
var backfillStartedAt = DateTime.UtcNow;
backfill.Start(enabled: true, scrollLimit: 30, backfillStartedAt, TimeSpan.FromMinutes(3));
backfill.BeginTopPass();
backfill.RegisterAttempt();
backfill.CancelPass();
var remainingBeforeRefresh = backfill.ScrollsRemaining;
var deadlineBeforeRefresh = backfill.DeadlineUtc;
var sessionBeforeRefresh = backfill.SessionId;
backfill.ResetPageContext(enabled: true);
Require(backfill.ScrollsRemaining == remainingBeforeRefresh && backfill.Attempts == 1,
    "页面刷新或 frame 替换不得清空未完成的启动回扫进度");
Require(backfill.DeadlineUtc == deadlineBeforeRefresh && backfill.CanContinue(true, 20, backfillStartedAt.AddSeconds(10)),
    "页面刷新后应沿用原回扫截止时间并继续历史扫描");
Require(backfill.SessionId == sessionBeforeRefresh,
    "页面基线重建不得孤立仍处于 active 状态的回扫脚本会话");
backfill.BeginTopPass();
Require(!backfill.CanExecutePending(true, 20, backfill.DeadlineUtc),
    "截止时间前排队但到期后才执行的回扫 pass 必须被取消");
Require(!backfill.CanExecutePending(true, 20, backfill.DeadlineUtc.AddMinutes(1)),
    "pending pass 的到期清理必须独立于任意较长的点赞冷却时间");
backfill.CancelPass();
backfill.Finish();
backfill.ResetPageContext(enabled: true);
Require(!backfill.CanContinue(true, 20, backfillStartedAt.AddSeconds(20)),
    "已完成的回扫不得因后续自动刷新而重新开始消耗额度");
restartSession.Start();
Require(!cooldownSettings.IsActionCooldownElapsed(cooldownAt.AddSeconds(2)),
    "停止后立即重启不得绕过最短操作间隔");
Require(cooldownSettings.IsActionCooldownElapsed(cooldownAt.AddSeconds(3)),
    "达到最短操作间隔后应允许下一次尝试");
restartSession.Stop();

var cooldownDirectory = Path.Combine(Path.GetTempPath(), "QzoneLikeAssistant.SmokeTests", Guid.NewGuid().ToString("N"));
var cooldownPath = Path.Combine(cooldownDirectory, "settings.json");
cooldownSettings.Save(cooldownPath);
var reloadedCooldownSettings = AppSettings.Load(cooldownPath);
Require(reloadedCooldownSettings.LastActionAtUtc == cooldownAt,
    "进程重启后必须保留最后一次真实尝试时间");
Require(!reloadedCooldownSettings.IsActionCooldownElapsed(cooldownAt.AddSeconds(2)),
    "重新加载设置后仍不得绕过冷却");
Directory.Delete(cooldownDirectory, true);

var previousContexts = new RefreshContextCheckpoint[]
{
    new(true, ["main-a", "main-b"]),
    new(false, ["frame-a", "frame-b"]),
    new(false, ["frame-c", "frame-d"])
};
var currentContexts = new RefreshContextCheckpoint[]
{
    new(true, ["main-a", "main-b"]),
    new(false, ["frame-c", "frame-d"]),
    new(false, ["frame-a", "frame-b"])
};
var contextMatches = RefreshContextMatcher.Match(previousContexts, currentContexts);
Require(contextMatches.Count == 3 &&
        contextMatches[0] == 0 && contextMatches[1] == 2 && contextMatches[2] == 1,
    "刷新上下文应按独立稳定锚点匹配，不能依赖 frame 顺序");
Require(RefreshContextMatcher.Match(
        [new(false, ["only-one"])],
        [new(false, ["only-one"])]).Count == 0,
    "只有一个共同锚点时应保守地视为上下文不明确");
Require(RefreshContextMatcher.Match(
        [new(false, ["same-a", "same-b"]), new(false, ["same-a", "same-b"])],
        [new(false, ["same-a", "same-b"])]).Count == 0,
    "多个 frame 都可匹配时不得选取歧义锚点");
Require(RefreshContextMatcher.Match(
        [new(true, ["cross-a", "cross-b"])],
        [new(false, ["cross-a", "cross-b"])]).Count == 0,
    "主页面锚点不得跨上下文匹配到 frame");

var navigation = new NavigationGeneration();
navigation.Start(100);
navigation.Start(101);
Require(!navigation.TryComplete(100) && navigation.InProgress && navigation.CurrentId == 101,
    "旧导航的完成事件不得清除最新导航状态");
Require(navigation.TryComplete(101) && !navigation.InProgress,
    "最新导航完成后必须解除扫描暂停");
navigation.MarkPending();
navigation.Start(102);
Require(navigation.TryComplete(102) && !navigation.InProgress,
    "孤立的最新 OperationCanceled 应能按导航代次解除扫描暂停");

var scripts = new Dictionary<string, string>
{
    ["start-attempt"] = LikeScript.BuildStartAttempt(settings, bounded, true, "new", "tracker-test", "token-test"),
    ["get-attempt"] = LikeScript.BuildGetAttemptResult("attempt-test", "token-test"),
    ["tracker"] = LikeScript.BuildInitializeTracker("tracker-test", "token-test"),
    ["tracker-snapshot"] = LikeScript.BuildGetTrackerSnapshot("tracker-test", "token-test"),
    ["tracker-arm"] = LikeScript.BuildArmTracker("tracker-test", "token-test"),
    ["refresh-capture"] = LikeScript.BuildCaptureRefreshKeys(),
    ["refresh-apply-arm"] = LikeScript.BuildApplyAndArmRefreshKeys(["old-1"], "tracker-test", "token-test"),
    ["invalidate"] = LikeScript.BuildInvalidateAutomation("token-test"),
    ["backfill-active"] = LikeScript.BuildSetBackfillActive("backfill-test", "token-test", true),
    ["backfill-inactive"] = LikeScript.BuildSetBackfillActive("backfill-test", "token-test", false),
    ["backfill-frame"] = LikeScript.BuildBackfillScroll("backfill-test", "token-test"),
    ["backfill-host"] = LikeScript.BuildBackfillScroll("backfill-test", "token-test", true),
    ["return-top"] = LikeScript.BuildReturnToTop("backfill-test")
};

foreach (var (name, script) in scripts)
{
    await CheckJavaScriptAsync(name, script);
}

var confirmed = LikeScript.ParseResult(
    """{"completed":true,"clicked":true,"attempted":true,"key":"k","preview":"p","error":null}""");
Require(confirmed is { Completed: true, Clicked: true, Attempted: true, Key: "k" }, "点赞确认结果必须能被解析");
var started = LikeScript.ParseStartResult(
    """{"started":true,"trackerMissing":false,"attemptId":"a","error":null}""");
Require(started is { Started: true, AttemptId: "a" }, "同步启动结果必须能被解析");
var refreshDiff = LikeScript.ParseRefreshDiffResult(
    """{"valid":true,"anchorFound":true,"newCount":2,"armed":true}""");
Require(refreshDiff is { Valid: true, AnchorFound: true, NewCount: 2, Armed: true }, "刷新差异结果必须能被解析");
Require(!LikeScript.BuildCaptureRefreshKeys().Contains("innerText", StringComparison.Ordinal) &&
        !LikeScript.BuildApplyAndArmRefreshKeys(["old-1"], "tracker-test", "token-test")
            .Contains("innerText", StringComparison.Ordinal),
    "刷新锚点脚本不得退化到不稳定的正文文本");
await CheckRefreshDiffBehaviorAsync();

Console.WriteLine("Smoke tests passed: defaults, V5 migration preservation, navigation generations, context-safe refresh matching, atomic refresh arm, refresh-safe backfill progress, persistent restart cooldown, stopped-race quota, session invalidation, FIFO eviction, generated JavaScript syntax.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task CheckJavaScriptAsync(string name, string script)
{
    var startInfo = new ProcessStartInfo("node")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--check");
    startInfo.ArgumentList.Add("-");

    using var process = Process.Start(startInfo) ??
        throw new InvalidOperationException("无法启动 Node.js 进行脚本语法检查");
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.StandardInput.WriteAsync(script);
    process.StandardInput.Close();
    await process.WaitForExitAsync();
    var error = await errorTask;
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"JavaScript {name} 语法检查失败：{error}");
}

static async Task CheckRefreshDiffBehaviorAsync()
{
    const string trackerId = "refresh-smoke-tracker";
    const string token = "refresh-smoke-token";
    var captureOutput = await RunJavaScriptAsync(BuildDomHarness(
        ["old-a", "old-b"], trackerId, token, LikeScript.BuildCaptureRefreshKeys()));
    using var captureJson = JsonDocument.Parse(captureOutput);
    var captured = captureJson.RootElement.GetProperty("result").EnumerateArray()
        .Select(element => element.GetString()).ToArray();
    Require(captured.SequenceEqual(["old-a", "old-b"]), "刷新前必须按页面顺序保存顶部锚点");

    var applyOutput = await RunJavaScriptAsync(BuildDomHarness(
        ["new-c", "old-a", "old-b"], trackerId, token,
        LikeScript.BuildApplyAndArmRefreshKeys(["old-a", "old-b"], trackerId, token)));
    using var applyJson = JsonDocument.Parse(applyOutput);
    var result = applyJson.RootElement.GetProperty("result");
    Require(result.GetProperty("valid").GetBoolean() &&
            result.GetProperty("anchorFound").GetBoolean() &&
            result.GetProperty("newCount").GetInt32() == 1 &&
            result.GetProperty("armed").GetBoolean() &&
            applyJson.RootElement.GetProperty("trackerArmed").GetBoolean(),
        "刷新后必须只识别旧锚点之前的新增动态");
    var newKeys = applyJson.RootElement.GetProperty("newKeys").EnumerateArray()
        .Select(element => element.GetString()).ToArray();
    Require(newKeys.SequenceEqual(["new-c"]), "旧锚点及其后的历史动态不得进入新动态队列");

    var missingOutput = await RunJavaScriptAsync(BuildDomHarness(
        ["unrelated-x"], trackerId, token,
        LikeScript.BuildApplyAndArmRefreshKeys(["old-a", "old-b"], trackerId, token)));
    using var missingJson = JsonDocument.Parse(missingOutput);
    var missing = missingJson.RootElement.GetProperty("result");
    Require(!missing.GetProperty("anchorFound").GetBoolean() &&
            missing.GetProperty("newCount").GetInt32() == 0 &&
            missing.GetProperty("armed").GetBoolean() &&
            missingJson.RootElement.GetProperty("trackerArmed").GetBoolean() &&
            missingJson.RootElement.GetProperty("newKeys").GetArrayLength() == 0,
        "找不到旧锚点时必须把当前页面全部按历史内容保护");
}

static string BuildDomHarness(
    string[] keys,
    string trackerId,
    string token,
    string expression)
{
    var keysJson = JsonSerializer.Serialize(keys);
    var trackerJson = JsonSerializer.Serialize(trackerId);
    var tokenJson = JsonSerializer.Serialize(token);
    return $$"""
    const keys = {{keysJson}};
    const items = keys.map(key => ({
      getAttribute: name => name === 'data-tid' ? key : null,
      id: '', innerText: key
    }));
    const buttons = items.map(item => ({ closest: () => item }));
    globalThis.document = { querySelectorAll: () => buttons };
    globalThis.__qzaAutomationSession = {{tokenJson}};
    globalThis.__qzaFeedTracker = {
      trackerId: {{trackerJson}}, automationToken: {{tokenJson}},
      newKeys: new Set(), newOrder: [], bootstrapComplete: false
    };
    const result = {{expression}};
    console.log(JSON.stringify({
      result,
      newKeys: [...globalThis.__qzaFeedTracker.newKeys],
      trackerArmed: globalThis.__qzaFeedTracker.bootstrapComplete === true
    }));
    """;
}

static async Task<string> RunJavaScriptAsync(string script)
{
    var startInfo = new ProcessStartInfo("node")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("-");
    using var process = Process.Start(startInfo) ??
        throw new InvalidOperationException("无法启动 Node.js 执行刷新差异测试");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.StandardInput.WriteAsync(script);
    process.StandardInput.Close();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"刷新差异 JavaScript 执行失败：{error}");
    return output.Trim();
}
