using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace QzoneLikeAssistant;

public partial class MainWindow : Window
{
    private const string QzoneHome = "https://qzone.qq.com/";
    private const int BackfillScrollLimit = 30;
    private const int BaselineStablePollsRequired = 3;
    private static readonly TimeSpan BaselineMinimumSettleTime = TimeSpan.FromSeconds(2);
    private readonly string appDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QzoneLikeAssistant");
    private readonly DispatcherTimer scanTimer = new();
    private readonly DispatcherTimer navigationWatchdog = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly List<CoreWebView2Frame> frames = [];
    private readonly BoundedKeySet processedKeys = new(1000);
    private readonly AutomationSession automationSession = new();
    private readonly AttemptQuotaLedger attemptQuotaLedger = new();
    private AppSettings settings;
    private bool running;
    private bool scanning;
    private int backfillScrollsRemaining;
    private int backfillLikes;
    private int backfillAttempts;
    private long backfillCounterRunId;
    private bool backfillPassPending;
    private bool backfillPassAtTop;
    private bool baselineReady;
    private bool trackersInitialized;
    private int baselineLastItemCount = -1;
    private int baselineStablePolls;
    private DateTime baselineArmNotBeforeUtc;
    private bool topBackfillComplete;
    private DateTime backfillDeadline;
    private string backfillSessionId = "";
    private string trackerId = "";
    private string networkDiagnostic = "";

    private string SettingsPath => Path.Combine(appDataDirectory, "settings.json");
    private string BrowserDataPath => Path.Combine(appDataDirectory, "WebView2");

    public MainWindow()
    {
        InitializeComponent();
        settings = AppSettings.Load(SettingsPath);
        LoadSettingsIntoControls();
        scanTimer.Tick += async (_, _) => await ScanOnceAsync();
        navigationWatchdog.Tick += (_, _) => ShowNavigationTimeout();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closing += (_, _) => StopAutomation("自动点赞已停止");
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            await DetectNetworkConfigurationAsync();
            var environment = await CoreWebView2Environment.CreateAsync(null, BrowserDataPath);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsAllowedAppUrl(args.Uri)) core.Navigate(args.Uri);
            };
            core.NavigationStarting += (_, args) =>
            {
                if (IsAllowedAppUrl(args.Uri))
                {
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target))
                    {
                        UpdateStatus(running ? "运行中" : "正在连接", $"正在打开 {target.Host}", running);
                    }
                    return;
                }

                args.Cancel = true;
                UpdateStatus("已拦截跳转", "应用阻止了非腾讯页面跳转", false);
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    navigationWatchdog.Stop();
                    Browser.Visibility = Visibility.Visible;
                    BrowserErrorOverlay.Visibility = Visibility.Collapsed;
                    if (running)
                    {
                        BeginBaselineRearm();
                    }
                    UpdateStatus(running ? "运行中" : "准备就绪", "页面已加载，请登录并进入好友动态", running);
                }
                else
                {
                    // WebView2 reports a superseded redirect as OperationCanceled.
                    // QQ login uses several redirects, so this is not a terminal failure.
                    if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        Browser.Visibility = Visibility.Visible;
                        BrowserErrorOverlay.Visibility = Visibility.Collapsed;
                        UpdateStatus(running ? "运行中" : "正在跳转", "QQ 登录正在继续跳转", running);
                        return;
                    }

                    navigationWatchdog.Stop();
                    Browser.Visibility = Visibility.Hidden;
                    BrowserErrorOverlay.Visibility = Visibility.Visible;
                    BrowserErrorText.Text = string.IsNullOrWhiteSpace(networkDiagnostic)
                        ? $"腾讯页面关闭了当前连接（{args.WebErrorStatus}）。请先确认 Microsoft Edge 能打开 qzone.qq.com。"
                        : $"腾讯页面连接失败（{args.WebErrorStatus}）。{networkDiagnostic}";
                    UpdateStatus("连接中断", $"QQ 空间加载失败：{args.WebErrorStatus}", false);
                }
            };
            core.FrameCreated += (_, args) => TrackFrame(args.Frame);
            StartNavigationWatchdog();
            core.Navigate(QzoneHome);
        }
        catch (Exception ex)
        {
            Browser.Visibility = Visibility.Hidden;
            BrowserErrorOverlay.Visibility = Visibility.Visible;
            BrowserErrorText.Text = ex.Message;
            UpdateStatus("初始化失败", "Edge 登录视图初始化失败", false);
        }
    }

    private void TrackFrame(CoreWebView2Frame frame)
    {
        frames.Add(frame);
        frame.FrameCreated += (_, args) => TrackFrame(args.Frame);
        frame.Destroyed += (_, _) =>
        {
            frames.Remove(frame);
            if (running) BeginBaselineRearm();
        };
        if (running) BeginBaselineRearm();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (running) StopAutomation("自动点赞已停止");
        else StartAutomation();
    }

    private void StartAutomation()
    {
        ReadSettingsFromControls();
        settings.ResetStatsIfNeeded();
        settings.Save(SettingsPath);
        var startedRun = automationSession.Start();
        backfillCounterRunId = startedRun.Id;
        running = true;
        processedKeys.Clear();
        backfillScrollsRemaining = settings.BackfillOnStart ? BackfillScrollLimit : 0;
        backfillLikes = 0;
        backfillAttempts = 0;
        backfillPassPending = false;
        backfillPassAtTop = false;
        BeginBaselineRearm();
        scanTimer.Interval = TimeSpan.FromSeconds(settings.ScanSeconds);
        scanTimer.Start();
        ToggleButton.Content = "停止自动点赞";
        ToggleButton.Background = new SolidColorBrush(Color.FromRgb(218, 74, 88));
        UpdateStatus("运行中", settings.BackfillOnStart ? "正在回扫启动前的已有动态" : "正在监听当前好友动态", true);
        _ = ScanOnceAsync();
    }

    private void StopAutomation(string message)
    {
        var stoppedToken = automationSession.Stop();
        running = false;
        scanTimer.Stop();
        if (!string.IsNullOrWhiteSpace(stoppedToken)) _ = InvalidateAutomationInPagesAsync(stoppedToken);
        if (backfillPassPending) _ = ReturnToTopAsync();
        backfillPassPending = false;
        ToggleButton.Content = "开启自动点赞";
        UpdateStatus("未开启", message, false);
    }

    private enum ScanResultState { None, Confirmed, Attempted }
    private readonly record struct AttemptExecution(LikeScript.StartResult? Start, LikeScript.Result? Result);

    private bool IsSessionActive(AutomationRun run) => running && automationSession.IsActive(run);

    private async Task ScanOnceAsync()
    {
        var run = automationSession.Current;
        if (!IsSessionActive(run) || scanning || Browser.CoreWebView2 is null) return;
        settings.ResetStatsIfNeeded();
        if (settings.TodayAttemptCount >= settings.DailyLimit)
        {
            StopAutomation("已达到今日点赞上限");
            return;
        }
        if (!settings.IsActionCooldownElapsed(DateTime.UtcNow)) return;

        var token = run.Token;
        scanning = true;
        try
        {
            if (!IsSessionActive(run)) return;
            var isBackfillPass = backfillPassPending;
            if (!isBackfillPass)
            {
                await ReturnToTopAsync(run);
                if (!IsSessionActive(run)) return;
            }

            if (!baselineReady)
            {
                await AdvanceBaselineAsync(run);
                return;
            }

            var scanMode = isBackfillPass ? (backfillPassAtTop ? "historical" : "any") : "new";
            var script = LikeScript.BuildStartAttempt(settings, processedKeys, viewportOnly: true,
                scanMode, trackerId, token);
            var sawInvalidSession = false;

            if (!IsSessionActive(run)) return;
            var execution = await ExecuteAttemptAsync(Browser.ExecuteScriptAsync, script, run,
                attemptId => RegisterAttemptStarted(attemptId, isBackfillPass, run));
            if (!IsSessionActive(run)) return;
            if (execution.Start?.Error == "start_result_invalid")
            {
                ApplyTransientCooldown();
                UpdateStatus("运行中", "页面未返回有效启动结果，本轮已停止并进入冷却", true);
                return;
            }
            if (execution.Start?.TrackerMissing == true)
            {
                BeginBaselineRearm();
                UpdateStatus("运行中", "页面结构发生变化，正在重新建立安全基线", true);
                return;
            }
            sawInvalidSession |= execution.Start?.Error == "session_invalid" ||
                execution.Result?.Error == "session_invalid";
            if (execution.Start?.Started == true)
            {
                HandleLikeResult(execution.Result, isBackfillPass, run);
                if (isBackfillPass) await CompleteBackfillPassAsync(run);
                return;
            }

            foreach (var frame in frames.ToArray())
            {
                if (!IsSessionActive(run)) return;
                try
                {
                    if (frame.IsDestroyed() != 0) continue;
                    execution = await ExecuteAttemptAsync(frame.ExecuteScriptAsync, script, run,
                        attemptId => RegisterAttemptStarted(attemptId, isBackfillPass, run));
                    if (!IsSessionActive(run)) return;
                    if (execution.Start?.Error == "start_result_invalid")
                    {
                        ApplyTransientCooldown();
                        UpdateStatus("运行中", "动态页面未返回有效启动结果，本轮已停止并进入冷却", true);
                        return;
                    }
                    if (execution.Start?.TrackerMissing == true)
                    {
                        BeginBaselineRearm();
                        UpdateStatus("运行中", "动态页面已替换，正在重新建立安全基线", true);
                        return;
                    }
                    sawInvalidSession |= execution.Start?.Error == "session_invalid" ||
                        execution.Result?.Error == "session_invalid";
                    if (execution.Start?.Started == true)
                    {
                        HandleLikeResult(execution.Result, isBackfillPass, run);
                        if (isBackfillPass) await CompleteBackfillPassAsync(run);
                        return;
                    }
                }
                catch
                {
                    // QQ Space replaces feed frames while updating.
                }
            }

            if (!IsSessionActive(run)) return;
            if (sawInvalidSession)
            {
                BeginBaselineRearm();
                UpdateStatus("运行中", "页面结构发生变化，正在重新建立安全基线", true);
                return;
            }

            if (isBackfillPass)
            {
                if (backfillPassAtTop) topBackfillComplete = true;
                await CompleteBackfillPassAsync(run);
                return;
            }

            if (!CanContinueBackfill())
            {
                if (backfillScrollsRemaining > 0)
                    FinishBackfill("回扫额度或时间已用完，继续优先监听新动态", run);
                return;
            }

            if (!topBackfillComplete)
            {
                backfillPassAtTop = true;
                backfillPassPending = true;
                UpdateStatus("回扫中", $"准备处理顶部已有动态（尝试 {backfillAttempts}/{BackfillBudget}）", true);
                return;
            }

            if (await ScrollForBackfillAsync(run))
            {
                if (!IsSessionActive(run)) return;
                backfillScrollsRemaining -= 1;
                backfillPassAtTop = false;
                backfillPassPending = true;
                UpdateStatus("回扫中", $"准备检查一屏旧动态（尝试 {backfillAttempts}/{BackfillBudget}）", true);
            }
            else if (IsSessionActive(run))
            {
                FinishBackfill("已有动态回扫完成，继续监听新动态", run);
            }
        }
        catch
        {
            if (!IsSessionActive(run)) return;
            ApplyTransientCooldown();
            if (backfillPassPending)
            {
                await ReturnToTopAsync(run);
                FinishBackfill("回扫页面发生变化，已退出回扫并继续监听新动态", run);
            }
            else
            {
                UpdateStatus("运行中", "本轮页面脚本中断，已进入冷却后再检测", true);
            }
        }
        finally
        {
            scanning = false;
        }
    }

    private async Task<AttemptExecution> ExecuteAttemptAsync(
        Func<string, Task<string>> executeScript,
        string startScript,
        AutomationRun run,
        Action<string> onStarted)
    {
        if (!IsSessionActive(run)) return default;
        var start = LikeScript.ParseStartResult(await executeScript(startScript)) ??
            new LikeScript.StartResult(false, false, null, "start_result_invalid");
        if (start?.Started == true && !string.IsNullOrWhiteSpace(start.AttemptId))
            onStarted(start.AttemptId);
        if (!IsSessionActive(run) || start?.Started != true || string.IsNullOrWhiteSpace(start.AttemptId))
            return new AttemptExecution(start, null);

        var getter = LikeScript.BuildGetAttemptResult(start.AttemptId, run.Token);
        for (var poll = 0; poll < 24; poll += 1)
        {
            await Task.Delay(140);
            if (!IsSessionActive(run)) return new AttemptExecution(start, null);
            LikeScript.Result? result = null;
            try
            {
                result = LikeScript.ParseResult(await executeScript(getter));
            }
            catch
            {
                // A transient getter failure does not start a second page attempt.
            }
            if (!IsSessionActive(run)) return new AttemptExecution(start, null);
            if (result?.Completed == true) return new AttemptExecution(start, result);
        }

        return new AttemptExecution(start, new LikeScript.Result(
            Completed: true,
            Clicked: false,
            Attempted: true,
            Key: null,
            Preview: null,
            Error: "attempt_poll_timeout"));
    }

    private async Task<bool> ScrollForBackfillAsync(AutomationRun run)
    {
        if (!IsSessionActive(run)) return false;
        var script = LikeScript.BuildBackfillScroll(backfillSessionId, run.Token);
        foreach (var frame in frames.ToArray())
        {
            if (!IsSessionActive(run)) return false;
            try
            {
                if (frame.IsDestroyed() != 0) continue;
                var result = await frame.ExecuteScriptAsync(script);
                if (!IsSessionActive(run)) return false;
                if (LikeScript.ParseBoolean(result)) return true;
            }
            catch
            {
                // QQ Space may replace the feed frame while older posts load.
            }
        }

        if (!IsSessionActive(run)) return false;
        try
        {
            var result = await Browser.ExecuteScriptAsync(script);
            return IsSessionActive(run) && LikeScript.ParseBoolean(result);
        }
        catch
        {
            return false;
        }
    }

    private void BeginBaselineRearm()
    {
        if (!running) return;
        baselineReady = false;
        trackersInitialized = false;
        baselineLastItemCount = -1;
        baselineStablePolls = 0;
        baselineArmNotBeforeUtc = DateTime.UtcNow + BaselineMinimumSettleTime;
        trackerId = Guid.NewGuid().ToString("N");
        topBackfillComplete = !settings.BackfillOnStart;
        backfillPassPending = false;
        backfillPassAtTop = false;
        backfillSessionId = Guid.NewGuid().ToString("N");
        backfillDeadline = DateTime.Now.AddMinutes(3);
    }

    private async Task AdvanceBaselineAsync(AutomationRun run)
    {
        var expectedTrackerId = trackerId;
        if (!trackersInitialized)
        {
            var initialized = await InitializeFeedTrackersAsync(run, expectedTrackerId);
            if (!IsSessionActive(run) || trackerId != expectedTrackerId) return;
            trackersInitialized = initialized;
            UpdateStatus("运行中", initialized
                ? "正在收集首屏历史动态，基线稳定后才会监听新增"
                : "等待进入好友动态后建立安全基线", true);
            return;
        }

        var snapshot = await GetFeedTrackerSnapshotAsync(run, expectedTrackerId);
        if (!IsSessionActive(run) || trackerId != expectedTrackerId) return;
        if (snapshot is not { Valid: true, ItemCount: > 0 })
        {
            baselineLastItemCount = -1;
            baselineStablePolls = 0;
            UpdateStatus("运行中", "等待好友动态非空快照，期间内容只记入历史基线", true);
            return;
        }

        if (snapshot.ItemCount == baselineLastItemCount) baselineStablePolls += 1;
        else
        {
            baselineLastItemCount = snapshot.ItemCount;
            baselineStablePolls = 1;
        }

        if (DateTime.UtcNow < baselineArmNotBeforeUtc || baselineStablePolls < BaselineStablePollsRequired)
        {
            UpdateStatus("运行中",
                $"正在确认历史基线稳定（{baselineStablePolls}/{BaselineStablePollsRequired}）", true);
            return;
        }

        var armed = await ArmFeedTrackersAsync(run, expectedTrackerId);
        if (!IsSessionActive(run) || trackerId != expectedTrackerId) return;
        if (!armed)
        {
            BeginBaselineRearm();
            UpdateStatus("运行中", "页面在基线武装时发生变化，正在重新建立安全基线", true);
            return;
        }

        baselineReady = true;
        UpdateStatus("运行中", "安全基线已武装，之后顶部新增的条目才视为新动态", true);
    }

    private async Task<bool> InitializeFeedTrackersAsync(AutomationRun run, string expectedTrackerId)
    {
        if (!IsSessionActive(run)) return false;
        var script = LikeScript.BuildInitializeTracker(expectedTrackerId, run.Token);
        try
        {
            await Browser.ExecuteScriptAsync(script);
            if (!IsSessionActive(run) || trackerId != expectedTrackerId) return false;
        }
        catch
        {
            return false;
        }

        foreach (var frame in frames.ToArray())
        {
            if (!IsSessionActive(run)) return false;
            try
            {
                if (frame.IsDestroyed() != 0) continue;
                await frame.ExecuteScriptAsync(script);
                if (!IsSessionActive(run) || trackerId != expectedTrackerId) return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private async Task<LikeScript.TrackerSnapshot?> GetFeedTrackerSnapshotAsync(
        AutomationRun run, string expectedTrackerId)
    {
        if (!IsSessionActive(run)) return null;
        var script = LikeScript.BuildGetTrackerSnapshot(expectedTrackerId, run.Token);
        var itemCount = 0;
        var validContexts = 0;
        try
        {
            var snapshot = LikeScript.ParseTrackerSnapshot(await Browser.ExecuteScriptAsync(script));
            if (snapshot?.Valid != true) return null;
            validContexts += 1;
            itemCount += snapshot.ItemCount;
        }
        catch
        {
            return null;
        }

        foreach (var frame in frames.ToArray())
        {
            if (!IsSessionActive(run) || trackerId != expectedTrackerId) return null;
            try
            {
                if (frame.IsDestroyed() != 0) continue;
                var snapshot = LikeScript.ParseTrackerSnapshot(await frame.ExecuteScriptAsync(script));
                if (snapshot?.Valid != true) return null;
                validContexts += 1;
                itemCount += snapshot.ItemCount;
            }
            catch
            {
                return null;
            }
        }

        return new LikeScript.TrackerSnapshot(validContexts > 0, itemCount, false);
    }

    private async Task<bool> ArmFeedTrackersAsync(AutomationRun run, string expectedTrackerId)
    {
        if (!IsSessionActive(run)) return false;
        var script = LikeScript.BuildArmTracker(expectedTrackerId, run.Token);
        var allArmed = true;
        try
        {
            allArmed &= LikeScript.ParseBoolean(await Browser.ExecuteScriptAsync(script));
        }
        catch
        {
            return false;
        }

        foreach (var frame in frames.ToArray())
        {
            if (!IsSessionActive(run) || trackerId != expectedTrackerId) return false;
            try
            {
                if (frame.IsDestroyed() == 0)
                    allArmed &= LikeScript.ParseBoolean(await frame.ExecuteScriptAsync(script));
            }
            catch
            {
                return false;
            }
        }

        return allArmed;
    }

    private async Task ReturnToTopAsync(AutomationRun? requiredRun = null)
    {
        bool Allowed() => requiredRun is null || IsSessionActive(requiredRun.Value);
        if (Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(backfillSessionId) || !Allowed()) return;
        var script = LikeScript.BuildReturnToTop(backfillSessionId);
        foreach (var frame in frames.ToArray())
        {
            if (!Allowed()) return;
            try
            {
                if (frame.IsDestroyed() == 0) await frame.ExecuteScriptAsync(script);
            }
            catch
            {
                // A replaced frame no longer needs to be restored.
            }
        }

        if (!Allowed()) return;
        try
        {
            await Browser.ExecuteScriptAsync(script);
        }
        catch
        {
            // Keep the normal scan timer alive if the page is navigating.
        }
    }

    private async Task InvalidateAutomationInPagesAsync(string stoppedToken)
    {
        if (Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(stoppedToken)) return;
        var script = LikeScript.BuildInvalidateAutomation(stoppedToken);
        var tasks = new List<Task>
        {
            IgnoreScriptErrorsAsync(() => Browser.ExecuteScriptAsync(script))
        };
        foreach (var frame in frames.ToArray())
        {
            if (frame.IsDestroyed() == 0)
                tasks.Add(IgnoreScriptErrorsAsync(() => frame.ExecuteScriptAsync(script)));
        }
        await Task.WhenAll(tasks);
    }

    private static async Task IgnoreScriptErrorsAsync(Func<Task<string>> executeScript)
    {
        try
        {
            await executeScript();
        }
        catch
        {
            // Closing, navigation or frame replacement already invalidates that context.
        }
    }

    private async Task CompleteBackfillPassAsync(AutomationRun run)
    {
        if (!IsSessionActive(run)) return;
        backfillPassPending = false;
        backfillPassAtTop = false;
        await ReturnToTopAsync(run);
        if (!IsSessionActive(run)) return;
        if (!CanContinueBackfill()) FinishBackfill("回扫已到限制，继续优先监听新动态", run);
        else UpdateStatus("运行中", "已返回顶部，优先检查新动态", true);
    }

    private int BackfillBudget => Math.Min(settings.BackfillLikeLimit,
        Math.Max(0, (int)Math.Floor(settings.DailyLimit * 0.30)));

    private bool CanContinueBackfill() => settings.BackfillOnStart &&
        backfillScrollsRemaining > 0 && backfillAttempts < BackfillBudget && DateTime.Now < backfillDeadline;

    private void FinishBackfill(string message, AutomationRun run)
    {
        if (!IsSessionActive(run)) return;
        backfillScrollsRemaining = 0;
        backfillPassPending = false;
        UpdateStatus("运行中", message, true);
    }

    private ScanResultState HandleLikeResult(LikeScript.Result? result, bool fromBackfill, AutomationRun run)
    {
        if (!IsSessionActive(run) || result is null) return ScanResultState.None;
        if (!result.Completed) return ScanResultState.None;
        if (result.Clicked != true || string.IsNullOrWhiteSpace(result.Key))
        {
            if (!result.Attempted) return ScanResultState.None;
            UpdateStatus("运行中", "已计入尝试额度，但未计入成功数且不去重；冷却后重试", true);
            return ScanResultState.Attempted;
        }

        AddProcessedKey(result.Key);
        settings.TodayCount += 1;
        if (fromBackfill) backfillLikes += 1;
        settings.Save(SettingsPath);
        UpdateStatus(fromBackfill ? "回扫中" : "运行中",
            $"已点赞{(fromBackfill ? "旧动态" : "新动态")}：{result.Preview ?? "一条好友动态"}", true);
        return ScanResultState.Confirmed;
    }

    private void RegisterAttemptStarted(string attemptId, bool fromBackfill, AutomationRun run)
    {
        if (!attemptQuotaLedger.Register(attemptId, settings)) return;
        if (fromBackfill && backfillCounterRunId == run.Id) backfillAttempts += 1;
        settings.Save(SettingsPath);
        if (IsSessionActive(run))
            UpdateStatus(fromBackfill ? "回扫中" : "运行中", "点赞已发出，正在确认页面状态", true);
    }

    private void ApplyTransientCooldown()
    {
        settings.RecordActionAt(DateTime.UtcNow);
        settings.Save(SettingsPath);
    }

    private void AddProcessedKey(string key)
    {
        processedKeys.Add(key);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        Browser.Visibility = Visibility.Visible;
        BrowserErrorOverlay.Visibility = Visibility.Collapsed;
        StartNavigationWatchdog();
        Browser.CoreWebView2.Reload();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        Browser.Visibility = Visibility.Visible;
        BrowserErrorOverlay.Visibility = Visibility.Collapsed;
        UpdateStatus("正在连接", "正在重新打开 QQ 空间", false);
        StartNavigationWatchdog();
        Browser.CoreWebView2.Navigate(QzoneHome);
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将退出 QQ 空间并删除本机保存的登录 Cookie，确定继续吗？", "确认清除登录",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        StopAutomation("正在清除本机登录状态");
        if (Browser.CoreWebView2 is null) return;
        await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        Browser.CoreWebView2.Navigate(QzoneHome);
        UpdateStatus("未开启", "登录状态已清除，请重新登录", false);
    }

    private void LoadSettingsIntoControls()
    {
        ScanSecondsBox.Text = settings.ScanSeconds.ToString();
        MinActionSecondsBox.Text = settings.MinActionSeconds.ToString();
        DailyLimitBox.Text = settings.DailyLimit.ToString();
        BackfillOnStartBox.IsChecked = settings.BackfillOnStart;
        BackfillLikeLimitBox.Text = settings.BackfillLikeLimit.ToString();
        IncludeKeywordsBox.Text = settings.IncludeKeywords;
        ExcludeKeywordsBox.Text = settings.ExcludeKeywords;
        TodayCount.Text = $"成功 {settings.TodayCount} / 尝试 {settings.TodayAttemptCount}";
    }

    private void ReadSettingsFromControls()
    {
        settings.ScanSeconds = ParseNumber(ScanSecondsBox.Text, settings.ScanSeconds);
        settings.MinActionSeconds = ParseNumber(MinActionSecondsBox.Text, settings.MinActionSeconds);
        settings.DailyLimit = ParseNumber(DailyLimitBox.Text, settings.DailyLimit);
        settings.BackfillOnStart = BackfillOnStartBox.IsChecked == true;
        settings.BackfillLikeLimit = ParseNumber(BackfillLikeLimitBox.Text, settings.BackfillLikeLimit);
        settings.IncludeKeywords = IncludeKeywordsBox.Text;
        settings.ExcludeKeywords = ExcludeKeywordsBox.Text;
        settings.Normalize();
        LoadSettingsIntoControls();
    }

    private void UpdateStatus(string title, string message, bool active)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusDot.Fill = new SolidColorBrush(active ? Color.FromRgb(48, 200, 119) : Color.FromRgb(101, 115, 140));
        TodayCount.Text = $"成功 {settings.TodayCount} / 尝试 {settings.TodayAttemptCount}";
    }

    private static int ParseNumber(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

    private void StartNavigationWatchdog()
    {
        navigationWatchdog.Stop();
        navigationWatchdog.Start();
    }

    private void ShowNavigationTimeout()
    {
        navigationWatchdog.Stop();
        Browser.Visibility = Visibility.Hidden;
        BrowserErrorOverlay.Visibility = Visibility.Visible;
        BrowserErrorText.Text =
            "QQ 空间登录页超过 15 秒没有响应。请先用 Microsoft Edge 打开 qzone.qq.com；" +
            "如果 Edge 也打不开，请关闭代理的 Fake-IP/TUN 模式或切换网络后重试。";
        UpdateStatus("连接超时", "QQ 空间登录主页没有响应", false);
    }

    private async Task DetectNetworkConfigurationAsync()
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("qzone.qq.com");
            var fakeIp = addresses.FirstOrDefault(IsProxyFakeIp);
            if (fakeIp is null) return;

            networkDiagnostic =
                $"检测到 qzone.qq.com 被解析为代理 Fake-IP {fakeIp}，但 WebView2 没有通过对应代理。" +
                "请启用代理的系统代理/TUN，或关闭 Fake-IP 模式后重试。";
            UpdateStatus("检测到代理 DNS", $"QQ 空间解析到了 {fakeIp}", false);
        }
        catch
        {
            // Navigation will report the actionable network error.
        }
    }

    private static bool IsProxyFakeIp(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static bool IsAllowedAppUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        // QQ login may pass through xui.ptlogin2, ssl.ptlogin2, connect,
        // user.qzone and other official subdomains. Restricting the list to
        // only a few hosts cancels the legitimate login redirect chain.
        return uri.Host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase);
    }
}
