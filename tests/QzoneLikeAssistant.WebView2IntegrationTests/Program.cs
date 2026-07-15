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
                Console.WriteLine("WebView2 integration test passed: synchronous attempt, polling, tracker-missing, cross-context host backfill scrolling, backfill reconciliation, explicit host baseline arm, atomic refresh-anchor diff, stable metadata keys, and DOM fallback keys.");
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
            "globalThis.__qzaFeedTracker.deferredKeys.has('data-tid:new-during-backfill')")),
            "回扫期间插入的动态应先进入待判定集合");
        await browser.ExecuteScriptAsync(LikeScript.BuildReturnToTop("backfill-test"));
        await Task.Delay(320);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.knownKeys.has('data-tid:new-during-backfill')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-tid:new-during-backfill')")),
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
            "globalThis.__qzaFeedTracker.knownKeys.has('data-tid:delayed-history')")),
            "第一批延迟渲染动态必须进入历史基线");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.knownKeys.has('data-tid:second-delayed-history')")),
            "第二批慢速渲染动态必须进入历史基线");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:delayed-history')")),
            "第一批延迟渲染动态不得进入新动态队列");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:second-delayed-history')")),
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
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:after-bootstrap-new')")),
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
        Require(refreshCheckpoint.SequenceEqual(["data-tid:refresh-old-a", "data-tid:refresh-old-b"]),
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
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:refresh-new-c')")),
            "旧锚点之前的动态必须进入新动态队列");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:refresh-old-a') || globalThis.__qzaFeedTracker.newKeys.has('data-tid:refresh-old-b')")),
            "旧锚点及其后的历史动态不得进入新动态队列");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item">
                <p>刷新后重建且没有稳定身份的历史项</p><button data-optype="like" aria-pressed="false">like</button>
              </article>
              <article class="feed_item" data-tid="old-a">
                <p>刷新前稳定锚点</p><button data-optype="like" aria-pressed="false">like</button>
              </article>`;
            true;
            """);
        const string fallbackRefreshTrackerId = "fallback-refresh-guard";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(fallbackRefreshTrackerId, token)));
        var fallbackRefreshDiff = LikeScript.ParseRefreshDiffResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildApplyAndArmRefreshKeys(
                ["data-tid:old-a"], fallbackRefreshTrackerId, token)));
        Require(fallbackRefreshDiff is { Valid: true, AnchorFound: true, NewCount: 0, Armed: true },
            $"无稳定身份的锚点前条目不得通过 refresh diff 进入新动态：{fallbackRefreshDiff}");
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              const key = globalThis.__qzaFeedTracker.keyFor(item, button);
              return globalThis.__qzaFeedTracker.knownKeys.has(key) &&
                !globalThis.__qzaFeedTracker.newKeys.has(key);
            })()
            """)),
            "refresh apply+arm 后无稳定身份的历史项只能保持 known");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-curkey="rebound-a">
                <p>刷新 settle 前即将被复用的条目 A</p><button data-optype="like" aria-pressed="false">like</button>
              </article>
              <article class="feed_item" data-tid="rebound-old-anchor">
                <p>刷新前稳定旧锚点</p><button data-optype="like" aria-pressed="false">like</button>
              </article>`;
            true;
            """);
        const string reboundBeforeApplyTrackerId = "rebound-before-refresh-apply";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(reboundBeforeApplyTrackerId, token)));
        Require(LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              item.setAttribute('data-curkey', 'rebound-b');
              item.querySelector('p').textContent = '虚拟列表复用后显示的历史条目 B';
              return item.getAttribute('data-curkey');
            })()
            """)) == "rebound-b",
            "refresh apply 前的节点复用脚本必须真实修改 DOM 身份为 B");
        await Task.Delay(120);
        Require(LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              return globalThis.__qzaFeedTracker.keyFor(item, item.querySelector('button'));
            })()
            """)) == "data-curkey:rebound-b",
            "refresh apply 前 MutationObserver 必须已把复用节点重绑定为 B");
        var reboundRefreshDiff = LikeScript.ParseRefreshDiffResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildApplyAndArmRefreshKeys(
                ["data-tid:rebound-old-anchor"], reboundBeforeApplyTrackerId, token)));
        Require(reboundRefreshDiff is { Valid: true, AnchorFound: true, NewCount: 0, Armed: true },
            $"已完成身份重绑定的历史 B 不得被后续 refresh apply 再次升级：{reboundRefreshDiff}");
        var reboundRefreshStart = LikeScript.ParseStartResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildStartAttempt(
                settings, Array.Empty<string>(), true, "new", reboundBeforeApplyTrackerId, token)));
        Require(reboundRefreshStart is { Started: false },
            $"refresh apply 后复用历史 B 不得发起 new 扫描点赞：{reboundRefreshStart}");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:rebound-a')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:rebound-b')")) &&
            LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.knownKeys.has('data-curkey:rebound-b')")),
            "身份先重绑定、refresh apply 后执行时，A/B 均不得保留 new 资格且 B 只能 known");

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
            "globalThis.__qzaFeedTracker.newKeys.has('data-tid:unrelated-x')")),
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
            document.body.innerHTML = `
              <article class="feed_item">
                <p>使用按钮稳定元数据的历史动态</p>
                <button data-optype="like" data-curkey="refresh-curkey-a" aria-pressed="false">like</button>
              </article>
              <article class="feed_item">
                <p>使用发布者与绝对时间复合键的历史动态</p>
                <button data-optype="like" data-opuin="123456789" data-abstime="1721000000" aria-pressed="false">like</button>
              </article>`;
            true;
            """);
        var metadataKeys = LikeScript.ParseStringArray(await browser.ExecuteScriptAsync(
            LikeScript.BuildCaptureRefreshKeys()));
        Require(metadataKeys.SequenceEqual(["data-curkey:refresh-curkey-a"]),
            $"刷新锚点只应使用带命名空间的稳定元数据，低熵 opuin+abstime 不得作为锚点：{string.Join(',', metadataKeys)}");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" id="history-without-id">
                <p>初始正文</p><button data-optype="like" aria-pressed="false">like 0</button>
              </article>`;
            document.querySelector('article').removeAttribute('id');
            true;
            """);
        const string fallbackTrackerId = "fallback-key-tracker";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(fallbackTrackerId, token)));
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
            LikeScript.BuildArmTracker(fallbackTrackerId, token))),
            "无稳定 ID 的 DOM 键测试应先显式武装 tracker");
        var fallbackKeyBefore = LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              return globalThis.__qzaFeedTracker.keyFor(item, button);
            })()
            """));
        await browser.ExecuteScriptAsync("""
            document.querySelector('article p').textContent = '正文和点赞数都发生变化';
            document.querySelector('article button').textContent = 'like 99';
            true;
            """);
        var fallbackKeyAfter = LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              return globalThis.__qzaFeedTracker.keyFor(item, button);
            })()
            """));
        Require(!string.IsNullOrWhiteSpace(fallbackKeyBefore) && fallbackKeyBefore == fallbackKeyAfter,
            "正文或点赞数变化不得改变同一动态的 DOM 生命周期键");

        await browser.ExecuteScriptAsync("""
            (() => {
              const old = document.querySelector('article');
              old.replaceWith(old.cloneNode(true));
              return true;
            })()
            """);
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              const key = globalThis.__qzaFeedTracker.keyFor(item, button);
              return globalThis.__qzaFeedTracker.knownKeys.has(key) &&
                !globalThis.__qzaFeedTracker.newKeys.has(key);
            })()
            """)),
            "无稳定 ID 的历史节点被克隆重建后只能登记为 known，不能判成新动态");

        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item">
                <p>无法证明身份的无 ID 插入项</p>
                <button data-optype="like" aria-pressed="false">like</button>
              </article>`);
            true;
            """);
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              const key = globalThis.__qzaFeedTracker.keyFor(item, button);
              return globalThis.__qzaFeedTracker.knownKeys.has(key) &&
                !globalThis.__qzaFeedTracker.newKeys.has(key);
            })()
            """)),
            "无稳定身份的新 DOM 节点无法排除历史重渲染，必须保守地只登记为 known");

        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item">
                <div data-curkey="nested-new-c"><p>身份位于按钮与 article 之间的新动态</p>
                  <button data-optype="like" aria-pressed="false">like</button></div>
              </article>`);
            true;
            """);
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:nested-new-c')")),
            "带唯一稳定身份的新增项应进入 newKeys，且应读取嵌套 identity 祖先");

        await browser.ExecuteScriptAsync("document.body.innerHTML = ''");
        const string armRaceTrackerId = "arm-boundary-race";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(armRaceTrackerId, token)));
        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-tid="before-arm-history">
                <p>arm 前已插入的历史动态</p><button data-optype="like" aria-pressed="false">like</button>
              </article>`);
            true;
            """);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildArmTracker(armRaceTrackerId, token))),
            "arm 边界竞态测试应立即显式武装 tracker");
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.knownKeys.has('data-tid:before-arm-history')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-tid:before-arm-history')")),
            "observer 回调不得因稍后 arm 而把 arm 前插入的历史项升级为新动态");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-curkey="identity-a">
                <p>属性变化测试历史项</p><button data-optype="like" aria-pressed="false">like</button>
              </article>`;
            true;
            """);
        const string attributeTrackerId = "attribute-change";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(attributeTrackerId, token)));
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildArmTracker(attributeTrackerId, token))),
            "属性变化测试应先武装 tracker");
        await browser.ExecuteScriptAsync(
            "document.querySelector('article').setAttribute('data-curkey', 'identity-b'); true;");
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.knownKeys.has('data-curkey:identity-a')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:identity-b')")),
            "同一历史节点的 identity 属性变化不得生成新的 newKey");

        await browser.ExecuteScriptAsync("document.body.innerHTML = ''");
        const string reusedNodeTrackerId = "reused-new-node";
        LikeScript.ParseInteger(await browser.ExecuteScriptAsync(
            LikeScript.BuildInitializeTracker(reusedNodeTrackerId, token)));
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                LikeScript.BuildArmTracker(reusedNodeTrackerId, token))),
            "复用新节点测试应先武装 tracker");
        await browser.ExecuteScriptAsync("""
            document.body.insertAdjacentHTML('afterbegin', `
              <article class="feed_item" data-curkey="reused-a">
                <p>最初识别为新动态 A</p><button data-optype="like" aria-pressed="false">like</button>
              </article>`);
            true;
            """);
        await Task.Delay(180);
        Require(LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:reused-a')")),
            "复用节点边界测试必须先确认 A 已获得 new 资格");
        Require(LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const item = document.querySelector('article');
              item.setAttribute('data-curkey', 'reused-b');
              item.querySelector('p').textContent = '虚拟列表复用后显示的历史动态 B';
              return item.getAttribute('data-curkey');
            })()
            """)) == "reused-b",
            "复用节点测试脚本必须真实修改 DOM 身份为 B");
        await Task.Delay(120);
        var reusedStateBeforeStart = LikeScript.ParseString(await browser.ExecuteScriptAsync("""
            (() => {
              const tracker = globalThis.__qzaFeedTracker;
              const item = document.querySelector('article');
              const button = item.querySelector('button');
              return JSON.stringify({
                key: tracker.keyFor(item, button),
                newKeys: [...tracker.newKeys],
                knownKeys: [...tracker.knownKeys],
                promotable: tracker.canPromoteForRefresh(item, button)
              });
            })()
            """));
        var reusedStart = LikeScript.ParseStartResult(await browser.ExecuteScriptAsync(
            LikeScript.BuildStartAttempt(
                settings, Array.Empty<string>(), true, "new", reusedNodeTrackerId, token)));
        Require(reusedStart is { Started: false },
            $"身份从 A 变为 B 后不得沿用 A 的 new 资格发起点赞：{reusedStart}; state={reusedStateBeforeStart}");
        Require(!LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:reused-a')")) &&
            !LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-curkey:reused-b')")) &&
            LikeScript.ParseBoolean(await browser.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.knownKeys.has('data-curkey:reused-b')")),
            "节点身份变化后必须撤销旧 A 的 new 资格，并把 B 仅登记为 known");

        await browser.ExecuteScriptAsync("""
            document.body.innerHTML = `
              <article class="feed_item" data-tid="same-value"><button data-optype="like">like</button></article>
              <article class="feed_item" data-fid="same-value"><button data-optype="like">like</button></article>`;
            true;
            """);
        var namespacedKeys = LikeScript.ParseStringArray(await browser.ExecuteScriptAsync(
            LikeScript.BuildCaptureRefreshKeys()));
        Require(namespacedKeys.SequenceEqual(["data-tid:same-value", "data-fid:same-value"]),
            $"不同属性类型的相同原始值必须使用命名空间避免碰撞：{string.Join(',', namespacedKeys)}");

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
              deferred: globalThis.__qzaFeedTracker.deferredKeys.has('data-tid:host-loaded-history'),
              known: globalThis.__qzaFeedTracker.knownKeys.has('data-tid:host-loaded-history'),
              isNew: globalThis.__qzaFeedTracker.newKeys.has('data-tid:host-loaded-history')
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
                "globalThis.__qzaFeedTracker.knownKeys.has('data-tid:host-loaded-history')")) &&
            !LikeScript.ParseBoolean(await feedFrame.ExecuteScriptAsync(
                "globalThis.__qzaFeedTracker.newKeys.has('data-tid:host-loaded-history')")),
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
