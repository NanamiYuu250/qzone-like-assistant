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
                Console.WriteLine("WebView2 integration test passed: synchronous attempt, polling, tracker-missing, cross-context host backfill scrolling, backfill reconciliation, explicit host baseline arm, atomic refresh-anchor diff and arm.");
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
        await browser.ExecuteScriptAsync("""
            globalThis.__qzaBackfillState = {
              sessionId: 'stale-session', automationToken: 'old-token',
              offset: 900, element: document.scrollingElement, active: true
            };
            true;
            """);
        var trackerCount = LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(trackerId, token)));
        Require(trackerCount == 1, $"跟踪器应登记 1 条初始动态，实际为 {trackerCount}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaBackfillState === null")),
            "新运行初始化 tracker 时必须清除旧 token 遗留的 active 回扫状态");
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
            "globalThis.__qzaFeedTracker.knownKeys.has('new-during-backfill')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('new-during-backfill')")),
            "回扫期间首次出现的条目回顶后必须按历史项保护，不能仅凭顶部位置判为新动态");

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

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-tid="refresh-old-a">
                <p>刷新前顶部动态 A</p><button title="赞" aria-pressed="false">赞</button>
              </article>
              <article class="feed_item" data-tid="refresh-old-b">
                <p>刷新前顶部动态 B</p><button title="赞" aria-pressed="false">赞</button>
              </article>`;
            true;
            """);
        const string beforeRefreshTrackerId = "before-refresh";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(beforeRefreshTrackerId, token)));
        var refreshCheckpoint = LikeScript.ParseStringArray(await browser.ExecuteScriptAsync(
            LikeScript.BuildCaptureRefreshKeys()));
        Require(refreshCheckpoint.SequenceEqual(["refresh-old-a", "refresh-old-b"]),
            $"刷新前应按顶部顺序保存动态锚点：{string.Join(',', refreshCheckpoint)}");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-tid="refresh-new-c">
                <p>刷新后新到达动态 C</p><button title="赞" aria-pressed="false">赞</button>
              </article>
              <article class="feed_item" data-tid="refresh-old-a">
                <p>刷新前顶部动态 A</p><button title="赞" aria-pressed="false">赞</button>
              </article>
              <article class="feed_item" data-tid="refresh-old-b">
                <p>刷新前顶部动态 B</p><button title="赞" aria-pressed="false">赞</button>
              </article>`;
            true;
            """);
        const string afterRefreshTrackerId = "after-refresh";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(afterRefreshTrackerId, token)));
        var refreshDiff = LikeScript.ParseRefreshDiffResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildApplyAndArmRefreshKeys(refreshCheckpoint, afterRefreshTrackerId, token)));
        Require(refreshDiff is { Valid: true, AnchorFound: true, NewCount: 1, Armed: true },
            $"刷新后只应识别旧锚点之前的一条新增动态：{refreshDiff}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('refresh-new-c')")),
            "旧锚点之前的动态必须进入新动态队列");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('refresh-old-a') || globalThis.__qzaFeedTracker.newKeys.has('refresh-old-b')")),
            "旧锚点及其后的历史动态不得进入新动态队列");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-tid="unrelated-x">
                <p>完全不同的页面内容</p><button title="赞" aria-pressed="false">赞</button>
              </article>`;
            true;
            """);
        const string missingAnchorTrackerId = "missing-refresh-anchor";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(missingAnchorTrackerId, token)));
        var missingAnchorDiff = LikeScript.ParseRefreshDiffResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildApplyAndArmRefreshKeys(refreshCheckpoint, missingAnchorTrackerId, token)));
        Require(missingAnchorDiff is { Valid: true, AnchorFound: false, NewCount: 0, Armed: true },
            "刷新后找不到旧锚点时必须保守地把整页视为历史内容");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('unrelated-x')")),
            "无旧锚点的页面内容不得被误判为新动态");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item">
                <p>只有正文、没有稳定 ID 的动态</p><button title="赞" aria-pressed="false">赞</button>
              </article>`;
            true;
            """);
        Require(LikeScript.ParseStringArray(await browser.ExecuteScriptAsync(
                LikeScript.BuildCaptureRefreshKeys())).Count == 0,
            "没有 data-tid/data-feedskey/data-unikey/id 时不得用正文生成刷新锚点");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `<div style="height:1800px">无 iframe 的普通长页面</div>`;
            document.scrollingElement.scrollTop = 0;
            globalThis.__qzaAutomationSession = 'integration-token';
            true;
            """);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildSetBackfillActive("no-frame-scroll-test", token, true))),
            "宿主滚动测试应先显式进入回扫状态");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildBackfillScroll("no-frame-scroll-test", token, true))),
            "没有可见 iframe 和点赞按钮的宿主页面不得滚动 document root");

        var frameReady = new TaskCompletionSource<CoreWebView2Frame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args)
        {
            if (!string.Equals(args.Frame.Name, "feed-frame", StringComparison.Ordinal)) return;
            args.Frame.NavigationCompleted += async (_, navigationArgs) =>
            {
                if (!navigationArgs.IsSuccess) return;
                for (var poll = 0; poll < 40 && !frameReady.Task.IsCompleted; poll += 1)
                {
                    try
                    {
                        if (LikeScript.ParseBoolean(await args.Frame.ExecuteScriptAsync(
                                "Boolean(document.querySelector('[data-tid=\"frame-old-a\"]'))")))
                        {
                            frameReady.TrySetResult(args.Frame);
                            return;
                        }
                    }
                    catch
                    {
                        // about:blank may be completing while srcdoc is replacing it.
                    }
                    await Task.Delay(50);
                }
            };
        }
        browser.CoreWebView2.FrameCreated += OnFrameCreated;
        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <div id="wrong-scroll" style="width:100%;height:420px;overflow-y:auto">
                <div style="height:1800px">
                  <iframe name="hidden-unrelated" title="隐藏的无关子页面" style="display:none"></iframe>
                </div>
              </div>
              <iframe id="feed-frame" name="feed-frame" title="动态内容子页面"
                style="display:block;width:100%;height:400px"></iframe>
              <div style="height:1800px">外层页面滚动区</div>`;
            const feedFrame = document.getElementById('feed-frame');
            feedFrame.srcdoc = `<!doctype html><html><body>
              <article class="feed_item" data-tid="frame-old-a">
                <p>iframe 初始历史动态</p><button title="赞" aria-pressed="false">赞</button>
              </article></body></html>`;
            window.addEventListener('scroll', () => {
              const doc = feedFrame.contentDocument;
              if (!doc || doc.querySelector('[data-tid="host-loaded-history"]')) return;
              doc.body.insertAdjacentHTML('afterbegin', `
                <article class="feed_item" data-tid="host-loaded-history">
                  <p>宿主滚动后加载的历史动态</p><button title="赞" aria-pressed="false">赞</button>
                </article>`);
            }, { once: true });
            document.scrollingElement.scrollTop = 0;
            globalThis.__qzaAutomationSession = 'integration-token';
            true;
            """);
        var feedFrame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.CoreWebView2.FrameCreated -= OnFrameCreated;
        const string crossContextTrackerId = "cross-context-backfill";
        var crossContextInitialCount = LikeScript.ParseInteger(await feedFrame.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(crossContextTrackerId, token)));
        Require(crossContextInitialCount == 1,
            $"目标 feed iframe 应含 1 条初始历史动态，实际为 {crossContextInitialCount}");
        Require(LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                LikeScript.BuildArmTracker(crossContextTrackerId, token))),
            "feed iframe 的初始历史基线应能武装");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildSetBackfillActive("host-scroll-test", token, true))) &&
            LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                LikeScript.BuildSetBackfillActive("host-scroll-test", token, true))),
            "实际宿主滚动前，host 与 feed iframe 必须同时进入回扫状态");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildBackfillScroll("host-scroll-test", token, false))),
            "普通 frame 没有点赞按钮时不得擅自滚动");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildBackfillScroll("host-scroll-test", token, true))),
            "点赞按钮位于子页面时，宿主页面仍应能推进外层动态滚动区");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "document.scrollingElement.scrollTop > 0")),
            "宿主回扫脚本必须实际推进外层页面滚动位置");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "document.getElementById('wrong-scroll').scrollTop === 0")),
            "含隐藏 iframe 的无关大滚动容器不得抢占可见 feed iframe 的宿主滚动");
        await Task.Delay(250);
        var crossContextState = await feedFrame.ExecuteScriptAsync("""
            (() => ({
              active: globalThis.__qzaBackfillState?.active === true,
              present: Boolean(document.querySelector('[data-tid="host-loaded-history"]')),
              deferred: globalThis.__qzaFeedTracker.deferredKeys.has('host-loaded-history'),
              known: globalThis.__qzaFeedTracker.knownKeys.has('host-loaded-history'),
              isNew: globalThis.__qzaFeedTracker.newKeys.has('host-loaded-history')
            }))()
            """);
        Require(crossContextState.Contains("\"active\":true", StringComparison.Ordinal) &&
                crossContextState.Contains("\"present\":true", StringComparison.Ordinal) &&
                crossContextState.Contains("\"deferred\":true", StringComparison.Ordinal) &&
                crossContextState.Contains("\"isNew\":false", StringComparison.Ordinal),
            $"宿主滚动触发的 iframe 历史节点必须在回扫期间延迟判定，不能进入 newKeys：{crossContextState}");
        await feedFrame.ExecuteScriptAsync(LikeScript.BuildReturnToTop("host-scroll-test", deactivate: false));
        await browser.ExecuteScriptAsync(LikeScript.BuildReturnToTop("host-scroll-test", deactivate: false));
        Require(LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                LikeScript.BuildSetBackfillActive("host-scroll-test", token, false))) &&
            LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildSetBackfillActive("host-scroll-test", token, false))),
            "回顶后应统一退出所有上下文的回扫状态");
        await Task.Delay(320);
        Require(LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.knownKeys.has('host-loaded-history')")) &&
            !LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('host-loaded-history')")),
            "回顶后的延迟项只能登记为 known 历史项，不能重新判成新动态");

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
