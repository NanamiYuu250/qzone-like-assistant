using System.Diagnostics;
using QzoneLikeAssistant;

var settings = new AppSettings();
settings.Normalize();
Require(settings.ScanSeconds == 1, "默认扫描间隔应为 1 秒");
Require(settings.MinActionSeconds == 3, "默认点赞冷却应为 3 秒");
Require(settings.DailyLimit == 300, "默认每日上限应为 300 次");
Require(settings.TodayAttemptCount == 0, "每日尝试数默认应为 0");
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

var scripts = new Dictionary<string, string>
{
    ["start-attempt"] = LikeScript.BuildStartAttempt(settings, bounded, true, "new", "tracker-test", "token-test"),
    ["get-attempt"] = LikeScript.BuildGetAttemptResult("attempt-test", "token-test"),
    ["tracker"] = LikeScript.BuildInitializeTracker("tracker-test", "token-test"),
    ["tracker-snapshot"] = LikeScript.BuildGetTrackerSnapshot("tracker-test", "token-test"),
    ["tracker-arm"] = LikeScript.BuildArmTracker("tracker-test", "token-test"),
    ["invalidate"] = LikeScript.BuildInvalidateAutomation("token-test"),
    ["backfill"] = LikeScript.BuildBackfillScroll("backfill-test", "token-test"),
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

Console.WriteLine("Smoke tests passed: defaults, persistent restart cooldown, stopped-race quota, session invalidation, FIFO eviction, result parsing, generated JavaScript syntax.");

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
