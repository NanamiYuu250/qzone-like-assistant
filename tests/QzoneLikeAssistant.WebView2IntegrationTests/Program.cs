using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QzoneLikeAssistant;
using System.IO;
using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2();
        var window = new Window
        {
            Title = "WebView2 integration test",
            Width = 640,
            Height = 480,
            Left = -10000,
            Top = -10000,
            Opacity = 0.01,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = browser
        };

        window.Loaded += async (_, _) =>
        {
            try
            {
                await RunIntegrationTestAsync(browser);
                Console.WriteLine("WebView2 integration test passed: synchronous attempt, polling, tracker-missing, backfill reconciliation, explicit host baseline arm.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                browser.Dispose();
                window.Close();
                app.Shutdown();
            }
        };

        window.Show();
        app.Run();
        return exitCode;
    }

    private static async Task RunIntegrationTestAsync(WebView2 browser)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "QzoneLikeAssistant.WebView2Tests", Guid.NewGuid().ToString("N"));
        var environment = await CoreWebView2Environment.CreateAsync(null, dataPath);
        await browser.EnsureCoreWebView2Async(environment);

        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
            navigation.TrySetResult(args.IsSuccess);
        browser.NavigationCompleted += OnNavigationCompleted;
        browser.NavigateToString("""
            <!doctype html>
            <html><head><style>
              html, body { margin: 0; width: 100%; height: 100%; }
              .feed_item { height: 220px; padding: 24px; box-sizing: border-box; }
              button { width: 100px; height: 40px; }
            </style></head><body>
              <article class="feed_item" data-tid="old-1">
                <p>本地集成测试动态</p>
                <button title="赞" aria-pressed="false"
                  onclick="window.clickCount=(window.clickCount||0)+1; setTimeout(() => { this.setAttribute('aria-pressed','true'); this.title='取消赞'; }, 120)">赞</button>
              </article>
            </body></html>
            """);
        Require(await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10)), "本地测试页面导航失败");
        browser.NavigationCompleted -= OnNavigationCompleted;

        const string trackerId = "integration-tracker";
        const string token = "integration-token";
        var trackerCount = LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(trackerId, token)));
        Require(trackerCount == 1, $"跟踪器应登记 1 条初始动态，实际为 {trackerCount}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            LikeScript.BuildArmTracker(trackerId, token))),
            "宿主显式确认初始历史基线后应能武装跟踪器");

        var settings = new AppSettings();
        settings.Normalize();
        var startJson = await browser.ExecuteScriptAsync(LikeScript.BuildStartAttempt(
            settings, Array.Empty<string>(), true, "historical", trackerId, token));
        Require(startJson != "{}", "ExecuteScriptAsync 不得返回 Promise 占位对象 {}");
        var start = LikeScript.ParseStartResult(startJson);
        Require(start is { Started: true } && !string.IsNullOrWhiteSpace(start.AttemptId),
            $"同步启动未返回 attemptId：{startJson}");
        var attemptId = start!.AttemptId!;
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync("window.clickCount === 1")),
            "真实点击必须在同步启动脚本内完成，不能留给页面自主延迟执行");
        var stoppedRaceSession = new AutomationSession();
        stoppedRaceSession.Start();
        stoppedRaceSession.Stop();
        var stoppedRaceSettings = new AppSettings();
        var stoppedRaceLedger = new AttemptQuotaLedger();
        Require(stoppedRaceLedger.Register(attemptId, stoppedRaceSettings) &&
                stoppedRaceSettings.TodayAttemptCount == 1,
            "同步点击返回后即使会话已停止，attempt 仍必须登记额度");

        LikeScript.Result? result = null;
        var getter = LikeScript.BuildGetAttemptResult(attemptId, token);
        for (var poll = 0; poll < 30; poll += 1)
        {
            await Task.Delay(100);
            result = LikeScript.ParseResult(await browser.ExecuteScriptAsync(getter));
            if (result?.Completed == true) break;
        }
        Require(result is { Completed: true, Clicked: true, Attempted: true },
            $"轮询未得到已确认结果：{result}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "document.querySelector('button').getAttribute('aria-pressed') === 'true'")),
            "测试按钮没有进入已点赞状态");

        await browser.ExecuteScriptAsync("""
            globalThis.__qzaBackfillState = {
              sessionId: 'backfill-test', automationToken: 'integration-token',
              offset: 0, element: document.scrollingElement, active: true
            };
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-tid="new-during-backfill">
                <p>回扫期间到达的新动态</p><button title="赞" aria-pressed="false">赞</button>
              </article>`);
            true;
            """);
        await Task.Delay(220);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.deferredKeys.has('new-during-backfill')")),
            "回扫期间插入的动态应先进入待判定集合");
        await browser.ExecuteScriptAsync(LikeScript.BuildReturnToTop("backfill-test"));
        await Task.Delay(320);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('new-during-backfill')")),
            "返回顶部后，顶部待判定动态应恢复为新动态");

        await browser.ExecuteScriptAsync("document.body.innerHTML = ''");
        const string bootstrapTrackerId = "empty-frame-bootstrap";
        var emptyCount = LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(bootstrapTrackerId, token)));
        Require(emptyCount == 0, "空 frame 初始化时不应存在动态基线");
        await browser.ExecuteScriptAsync("""
            setTimeout(() => document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-tid="delayed-history">
                <p>frame 首批延迟渲染的历史动态</p><button title="赞" aria-pressed="false">赞</button>
              </article>`), 100);
            true;
            """);
        await Task.Delay(600);
        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-tid="second-delayed-history">
                <p>frame 第二批慢速渲染的历史动态</p><button title="赞" aria-pressed="false">赞</button>
              </article>`);
            true;
            """);
        await Task.Delay(240);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.knownKeys.has('delayed-history')")),
            "第一批延迟渲染动态必须进入历史基线");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.knownKeys.has('second-delayed-history')")),
            "第二批慢速渲染动态必须进入历史基线");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('delayed-history')")),
            "第一批延迟渲染动态不得进入新动态队列");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('second-delayed-history')")),
            "宿主武装前，第二批慢速历史动态也不得进入新动态队列");
        var unarmedSnapshot = LikeScript.ParseTrackerSnapshot(await browser.ExecuteScriptAsync(
            LikeScript.BuildGetTrackerSnapshot(bootstrapTrackerId, token)));
        Require(unarmedSnapshot is { Valid: true, ItemCount: 2, Armed: false },
            $"固定页面定时器不得自动武装跟踪器：{unarmedSnapshot}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            LikeScript.BuildArmTracker(bootstrapTrackerId, token))),
            "宿主确认非空快照稳定后应显式武装跟踪器");

        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-tid="after-bootstrap-new">
                <p>宿主武装后的新动态</p><button title="赞" aria-pressed="false">赞</button>
              </article>`);
            true;
            """);
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('after-bootstrap-new')")),
            "宿主显式武装后的顶部插入项应进入新动态队列");

        await browser.ExecuteScriptAsync("globalThis.__qzaFeedTracker = null");
        var missing = LikeScript.ParseStartResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildStartAttempt(settings, Array.Empty<string>(), true, "new", bootstrapTrackerId, token)));
        Require(missing is { Started: false, TrackerMissing: true },
            "跟踪器丢失必须通过同步启动结果报告");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
