using System.Text.Json;

namespace QzoneLikeAssistant;

internal static class LikeScript
{
    internal sealed record Result(
        bool Completed,
        bool Clicked,
        bool Attempted,
        string? Key,
        string? Preview,
        string? Error);

    internal sealed record StartResult(
        bool Started,
        bool TrackerMissing,
        string? AttemptId,
        string? Error);

    internal sealed record TrackerSnapshot(bool Valid, int ItemCount, bool Armed);

    internal sealed record RefreshDiffResult(bool Valid, bool AnchorFound, int NewCount, bool Armed);

    public static string BuildStartAttempt(
        AppSettings settings,
        IEnumerable<string> processedKeys,
        bool viewportOnly,
        string scanMode,
        string trackerId,
        string automationToken)
    {
        var config = JsonSerializer.Serialize(new
        {
            includeKeywords = SplitKeywords(settings.IncludeKeywords),
            excludeKeywords = SplitKeywords(settings.ExcludeKeywords),
            processedKeys = processedKeys.TakeLast(1000).ToArray(),
            viewportOnly,
            scanMode,
            trackerId,
            automationToken
        });

        return $$"""
        (() => {
          const config = {{config}};
          const empty = (extra = {}) => ({
            started: false, trackerMissing: false, attemptId: null, error: null, ...extra
          });
          if (globalThis.__qzaAutomationSession !== config.automationToken) {
            return empty({ error: 'session_invalid' });
          }

          const tracker = globalThis.__qzaFeedTracker;
          if (!tracker || tracker.trackerId !== config.trackerId ||
              tracker.automationToken !== config.automationToken) {
            return empty({ trackerMissing: true });
          }

          const processed = new Set(config.processedKeys);
          const buttonSelector = [
            'a.qz_like_btn_v3', '.qz_like_btn_v3', '[data-optype="like"]',
            '[data-op="like"]', '[data-action="like"]', 'a[title="赞"]',
            'button[aria-label="赞"]', 'button[title="赞"]'
          ].join(',');
          const itemSelector = [
            '[data-tid]', '[data-feedskey]', '[data-unikey]', '.f-single',
            '.feed_item', '.feed-item', '.qz-feed', 'article'
          ].join(',');
          const normalize = value => String(value || '').trim().toLocaleLowerCase('zh-CN');
          const include = config.includeKeywords.map(normalize);
          const exclude = config.excludeKeywords.map(normalize);
          const visible = element => {
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
          };
          const inViewport = element => {
            const rect = element.getBoundingClientRect();
            return rect.bottom >= 0 && rect.top <= (innerHeight || document.documentElement.clientHeight);
          };
          const liked = button => {
            const label = normalize([button.textContent, button.title, button.getAttribute('aria-label')].join(' '));
            const classes = normalize(button.className);
            return button.getAttribute('aria-pressed') === 'true' ||
              /(^|\s)(liked|selected|item-on)(\s|$)/.test(classes) ||
              label.includes('取消赞') || label.includes('已赞');
          };
          const matches = text => {
            const value = normalize(text);
            if (exclude.some(keyword => value.includes(keyword))) return false;
            return include.length === 0 || include.some(keyword => value.includes(keyword));
          };
          const keyFor = item => item.getAttribute('data-tid') ||
            item.getAttribute('data-feedskey') || item.getAttribute('data-unikey') ||
            item.id || normalize(item.innerText).slice(0, 160);
          const attempts = globalThis.__qzaLikeAttempts ||= new Map();
          while (attempts.size > 100) attempts.delete(attempts.keys().next().value);

          for (const button of document.querySelectorAll(buttonSelector)) {
            if (!visible(button) || liked(button)) continue;
            const item = button.closest(itemSelector);
            if (!item || !visible(item) || (config.viewportOnly && !inViewport(item))) continue;
            const text = item.innerText || item.textContent || '';
            if (!matches(text)) continue;
            const key = keyFor(item);
            if (!key || processed.has(key)) continue;

            const isNew = tracker.newKeys.has(key);
            const isKnown = tracker.knownKeys.has(key);
            if (config.scanMode === 'new' && !isNew) continue;
            if (config.scanMode === 'historical' && (!isKnown || isNew)) continue;
            if (globalThis.__qzaAutomationSession !== config.automationToken) {
              return empty({ error: 'session_invalid' });
            }

            const preview = String(text).replace(/\s+/g, ' ').trim().slice(0, 60);
            const clickTarget = button.isConnected ? button : item.querySelector(buttonSelector);
            if (!clickTarget || liked(clickTarget)) {
              tracker.newKeys.delete(key);
              continue;
            }
            const attemptId = (crypto.randomUUID ? crypto.randomUUID() :
              `${Date.now()}-${Math.random().toString(16).slice(2)}`);
            const attempt = {
              completed: false, clicked: false, attempted: true,
              key, preview, error: null
            };
            attempts.set(attemptId, attempt);
            clickTarget.click();
            const deadline = Date.now() + 2600;
            const verify = () => {
              if (globalThis.__qzaAutomationSession !== config.automationToken) {
                attempt.completed = true;
                attempt.error = 'session_stopped';
                return;
              }
              const currentButton = clickTarget.isConnected ? clickTarget : item.querySelector(buttonSelector);
              if (currentButton && liked(currentButton)) {
                tracker.newKeys.delete(key);
                attempt.completed = true;
                attempt.clicked = true;
                return;
              }
              if (Date.now() >= deadline) {
                attempt.completed = true;
                attempt.error = 'like_not_confirmed';
                return;
              }
              setTimeout(verify, 160);
            };
            setTimeout(verify, 160);
            return { started: true, trackerMissing: false, attemptId, error: null };
          }
          return empty();
        })()
        """;
    }

    public static string BuildGetAttemptResult(string attemptId, string automationToken)
    {
        var attemptJson = JsonSerializer.Serialize(attemptId);
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const attemptId = {{attemptJson}};
          const automationToken = {{tokenJson}};
          if (globalThis.__qzaAutomationSession !== automationToken) {
            return {
              completed: true, clicked: false, attempted: false,
              key: null, preview: null, error: 'session_invalid'
            };
          }
          const attempt = globalThis.__qzaLikeAttempts?.get(attemptId);
          return attempt || {
            completed: true, clicked: false, attempted: false,
            key: null, preview: null, error: 'attempt_missing'
          };
        })()
        """;
    }

    public static string BuildInitializeTracker(string trackerId, string automationToken)
    {
        var trackerJson = JsonSerializer.Serialize(trackerId);
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const trackerId = {{trackerJson}};
          const automationToken = {{tokenJson}};
          globalThis.__qzaAutomationSession = automationToken;
          if (globalThis.__qzaFeedTracker?.observer) {
            globalThis.__qzaFeedTracker.observer.disconnect();
          }

          const buttonSelector = [
            'a.qz_like_btn_v3', '.qz_like_btn_v3', '[data-optype="like"]',
            '[data-op="like"]', '[data-action="like"]', 'a[title="赞"]',
            'button[aria-label="赞"]', 'button[title="赞"]'
          ].join(',');
          const itemSelector = [
            '[data-tid]', '[data-feedskey]', '[data-unikey]', '.f-single',
            '.feed_item', '.feed-item', '.qz-feed', 'article'
          ].join(',');
          const normalize = value => String(value || '').trim().toLocaleLowerCase('zh-CN');
          const keyFor = item => item.getAttribute('data-tid') ||
            item.getAttribute('data-feedskey') || item.getAttribute('data-unikey') ||
            item.id || normalize(item.innerText).slice(0, 160);
          const state = {
            trackerId,
            automationToken,
            knownKeys: new Set(),
            knownOrder: [],
            newKeys: new Set(),
            newOrder: [],
            bootstrapComplete: false,
            deferredKeys: new Map(),
            reconcileDeferred: null,
            observer: null
          };

          const remember = (key, isNew) => {
            if (!key || state.knownKeys.has(key)) return false;
            state.knownKeys.add(key);
            state.knownOrder.push(key);
            if (isNew) {
              state.newKeys.add(key);
              state.newOrder.push(key);
            }
            while (state.knownOrder.length > 5000) {
              const oldest = state.knownOrder.shift();
              state.knownKeys.delete(oldest);
              state.newKeys.delete(oldest);
            }
            while (state.newOrder.length > 500) {
              state.newKeys.delete(state.newOrder.shift());
            }
            return true;
          };

          const isNearTop = item => {
            const viewportHeight = innerHeight || document.documentElement.clientHeight || 0;
            const rect = item.getBoundingClientRect();
            if (rect.bottom < 0 || rect.top > viewportHeight * 0.8) return false;
            for (let parent = item.parentElement; parent; parent = parent.parentElement) {
              const style = getComputedStyle(parent);
              if ((style.overflowY === 'auto' || style.overflowY === 'scroll') &&
                  parent.scrollHeight > parent.clientHeight + 120) {
                return parent.scrollTop < 160;
              }
            }
            return !document.scrollingElement || document.scrollingElement.scrollTop < 160;
          };

          const registerButtons = (root, allowNew, deferDuringBackfill = false) => {
            if (!root) return;
            const buttons = [];
            if (root.nodeType === Node.ELEMENT_NODE && root.matches(buttonSelector)) buttons.push(root);
            if (root.querySelectorAll) buttons.push(...root.querySelectorAll(buttonSelector));
            for (const button of buttons) {
              const item = button.closest(itemSelector);
              if (!item) continue;
              const key = keyFor(item);
              const backfilling = globalThis.__qzaBackfillState?.active === true;
              if ((deferDuringBackfill || backfilling) && key && !state.knownKeys.has(key)) {
                state.deferredKeys.set(key, Date.now());
                if (!backfilling) setTimeout(() => state.reconcileDeferred?.(), 0);
                continue;
              }
              remember(key, state.bootstrapComplete && allowNew && !backfilling && isNearTop(item));
            }
          };

          state.reconcileDeferred = () => {
            if (globalThis.__qzaBackfillState?.active === true || state.deferredKeys.size === 0) return;
            const visibleItems = new Map();
            for (const button of document.querySelectorAll(buttonSelector)) {
              const item = button.closest(itemSelector);
              if (!item) continue;
              const key = keyFor(item);
              if (key) visibleItems.set(key, item);
            }
            const now = Date.now();
            for (const [key, createdAt] of [...state.deferredKeys]) {
              const item = visibleItems.get(key);
              if (item) {
                remember(key, state.bootstrapComplete && isNearTop(item));
                state.deferredKeys.delete(key);
              } else if (now - createdAt > 15000) {
                state.deferredKeys.delete(key);
              }
            }
          };

          registerButtons(document, false);
          state.observer = new MutationObserver(mutations => {
            const wasBackfilling = globalThis.__qzaBackfillState?.active === true;
            const allowNew = globalThis.__qzaAutomationSession === automationToken && !wasBackfilling;
            const roots = [];
            for (const mutation of mutations) {
              for (const node of mutation.addedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) roots.push(node);
              }
            }
            if (roots.length === 0) return;
            setTimeout(() => {
              if (globalThis.__qzaAutomationSession !== automationToken) return;
              for (const root of roots) registerButtons(root, allowNew, wasBackfilling);
            }, 80);
          });
          if (document.documentElement) {
            state.observer.observe(document.documentElement, { childList: true, subtree: true });
          }
          globalThis.__qzaFeedTracker = state;
          return state.knownKeys.size;
        })()
        """;
    }

    public static string BuildGetTrackerSnapshot(string trackerId, string automationToken)
    {
        var trackerJson = JsonSerializer.Serialize(trackerId);
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const tracker = globalThis.__qzaFeedTracker;
          const valid = globalThis.__qzaAutomationSession === {{tokenJson}} &&
            tracker?.trackerId === {{trackerJson}} && tracker?.automationToken === {{tokenJson}};
          return valid
            ? { valid: true, itemCount: tracker.knownKeys.size, armed: tracker.bootstrapComplete === true }
            : { valid: false, itemCount: 0, armed: false };
        })()
        """;
    }

    public static string BuildCaptureRefreshKeys(int maxKeys = 60)
    {
        maxKeys = Math.Clamp(maxKeys, 1, 200);
        return $$"""
        (() => {
          const buttonSelector = [
            'a.qz_like_btn_v3', '.qz_like_btn_v3', '[data-optype="like"]',
            '[data-op="like"]', '[data-action="like"]', 'a[title="赞"]',
            'button[aria-label="赞"]', 'button[title="赞"]'
          ].join(',');
          const itemSelector = [
            '[data-tid]', '[data-feedskey]', '[data-unikey]', '.f-single',
            '.feed_item', '.feed-item', '.qz-feed', 'article'
          ].join(',');
          const keyFor = item => item.getAttribute('data-tid') ||
            item.getAttribute('data-feedskey') || item.getAttribute('data-unikey') ||
            item.id || null;
          const keys = [];
          const seen = new Set();
          for (const button of document.querySelectorAll(buttonSelector)) {
            const item = button.closest(itemSelector);
            const key = item && keyFor(item);
            if (!key || seen.has(key)) continue;
            seen.add(key);
            keys.push(key);
            if (keys.length >= {{maxKeys}}) break;
          }
          return keys;
        })()
        """;
    }

    public static string BuildApplyAndArmRefreshKeys(
        IEnumerable<string> previousTopKeys,
        string trackerId,
        string automationToken)
    {
        var config = JsonSerializer.Serialize(new
        {
            previousTopKeys = previousTopKeys.Take(200).ToArray(),
            trackerId,
            automationToken
        });
        return $$"""
        (() => {
          const config = {{config}};
          const tracker = globalThis.__qzaFeedTracker;
          if (globalThis.__qzaAutomationSession !== config.automationToken ||
              tracker?.trackerId !== config.trackerId ||
              tracker?.automationToken !== config.automationToken) {
             return { valid: false, anchorFound: false, newCount: 0, armed: false };
          }
          const buttonSelector = [
            'a.qz_like_btn_v3', '.qz_like_btn_v3', '[data-optype="like"]',
            '[data-op="like"]', '[data-action="like"]', 'a[title="赞"]',
            'button[aria-label="赞"]', 'button[title="赞"]'
          ].join(',');
          const itemSelector = [
            '[data-tid]', '[data-feedskey]', '[data-unikey]', '.f-single',
            '.feed_item', '.feed-item', '.qz-feed', 'article'
          ].join(',');
          const keyFor = item => item.getAttribute('data-tid') ||
            item.getAttribute('data-feedskey') || item.getAttribute('data-unikey') ||
            item.id || null;
          const oldKeys = new Set(config.previousTopKeys);
          const currentKeys = [];
          const seen = new Set();
          for (const button of document.querySelectorAll(buttonSelector)) {
            const item = button.closest(itemSelector);
            const key = item && keyFor(item);
            if (!key || seen.has(key)) continue;
            seen.add(key);
            currentKeys.push(key);
            if (currentKeys.length >= 200) break;
          }
          const anchorIndex = currentKeys.findIndex(key => oldKeys.has(key));
          if (anchorIndex < 0) {
            tracker.bootstrapComplete = true;
            tracker.reconcileDeferred?.();
            return { valid: true, anchorFound: false, newCount: 0, armed: true };
          }
          let newCount = 0;
          for (const key of currentKeys.slice(0, Math.min(anchorIndex, 20))) {
            if (tracker.newKeys.has(key)) continue;
            tracker.newKeys.add(key);
            tracker.newOrder.push(key);
            newCount += 1;
          }
          while (tracker.newOrder.length > 500) {
            tracker.newKeys.delete(tracker.newOrder.shift());
          }
          tracker.bootstrapComplete = true;
          tracker.reconcileDeferred?.();
          return { valid: true, anchorFound: true, newCount, armed: true };
        })()
        """;
    }

    public static string BuildArmTracker(string trackerId, string automationToken)
    {
        var trackerJson = JsonSerializer.Serialize(trackerId);
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const tracker = globalThis.__qzaFeedTracker;
          if (globalThis.__qzaAutomationSession !== {{tokenJson}} ||
              tracker?.trackerId !== {{trackerJson}} || tracker?.automationToken !== {{tokenJson}}) {
            return false;
          }
          tracker.bootstrapComplete = true;
          tracker.reconcileDeferred?.();
          return true;
        })()
        """;
    }

    public static string BuildInvalidateAutomation(string automationToken)
    {
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const automationToken = {{tokenJson}};
          if (globalThis.__qzaAutomationSession === automationToken) {
            globalThis.__qzaAutomationSession = null;
          }
          if (globalThis.__qzaFeedTracker?.automationToken === automationToken) {
            globalThis.__qzaFeedTracker.observer?.disconnect();
            globalThis.__qzaFeedTracker = null;
          }
          if (globalThis.__qzaBackfillState?.automationToken === automationToken) {
            globalThis.__qzaBackfillState.active = false;
          }
          return true;
        })()
        """;
    }

    public static string BuildBackfillScroll(string sessionId, string automationToken)
    {
        var sessionJson = JsonSerializer.Serialize(sessionId);
        var tokenJson = JsonSerializer.Serialize(automationToken);
        return $$"""
        (() => {
          const sessionId = {{sessionJson}};
          const automationToken = {{tokenJson}};
          if (globalThis.__qzaAutomationSession !== automationToken) return false;
          const buttonSelector = [
            'a.qz_like_btn_v3', '.qz_like_btn_v3', '[data-optype="like"]',
            '[data-op="like"]', '[data-action="like"]', 'a[title="赞"]',
            'button[aria-label="赞"]', 'button[title="赞"]'
          ].join(',');
          if (!document.querySelector(buttonSelector)) return false;
          if (!globalThis.__qzaBackfillState || globalThis.__qzaBackfillState.sessionId !== sessionId) {
            globalThis.__qzaBackfillState = {
              sessionId, automationToken, offset: 0, element: null, active: false
            };
          }
          const state = globalThis.__qzaBackfillState;
          state.active = true;
          const candidates = [document.scrollingElement, document.documentElement, document.body];
          for (const element of document.querySelectorAll('*')) {
            const style = getComputedStyle(element);
            if ((style.overflowY === 'auto' || style.overflowY === 'scroll') &&
                element.scrollHeight > element.clientHeight + 120 && element.clientHeight > 240) {
              candidates.push(element);
            }
          }

          const unique = [...new Set(candidates.filter(Boolean))]
            .sort((a, b) => (b.clientHeight * b.clientWidth) - (a.clientHeight * a.clientWidth));
          const ordered = state.element && state.element.isConnected
            ? [state.element, ...unique.filter(element => element !== state.element)]
            : unique;
          for (const element of ordered) {
            const before = element.scrollTop;
            const step = Math.max(Math.floor(element.clientHeight * 0.78), 520);
            const maximum = element.scrollHeight - element.clientHeight;
            element.scrollTop = Math.min(Math.max(state.offset + step, before + step), maximum);
            if (element.scrollTop > before + 2) {
              state.element = element;
              state.offset = element.scrollTop;
              return true;
            }
          }
          state.active = false;
          return false;
        })()
        """;
    }

    public static string BuildReturnToTop(string sessionId)
    {
        var sessionJson = JsonSerializer.Serialize(sessionId);
        return $$"""
        (() => {
          const sessionId = {{sessionJson}};
          const state = globalThis.__qzaBackfillState;
          const candidates = [
            state && state.sessionId === sessionId ? state.element : null,
            document.scrollingElement, document.documentElement, document.body
          ];
          let moved = false;
          for (const element of [...new Set(candidates.filter(Boolean))]) {
            if (!element.isConnected) continue;
            moved = moved || element.scrollTop > 2;
            element.scrollTop = 0;
          }
          if (state && state.sessionId === sessionId) state.active = false;
          setTimeout(() => globalThis.__qzaFeedTracker?.reconcileDeferred?.(), 120);
          return moved;
        })()
        """;
    }

    public static bool ParseBoolean(string json) =>
        bool.TryParse(json, out var value) && value;

    public static int ParseInteger(string json) =>
        int.TryParse(json, out var value) ? value : 0;

    public static IReadOnlyList<string> ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static RefreshDiffResult? ParseRefreshDiffResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RefreshDiffResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public static Result? ParseResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Result>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public static StartResult? ParseStartResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StartResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public static TrackerSnapshot? ParseTrackerSnapshot(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TrackerSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string[] SplitKeywords(string text) => text
        .Split([',', '，', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
