# QQ空间点赞助手

现代 WPF Windows 桌面程序。右侧使用系统 Edge WebView2 直接打开腾讯官方 QQ 空间，用户可在 App 内扫码或登录；左侧可开启自动点赞并设置扫描间隔、最短点赞间隔、每日上限、启动回扫和关键词过滤。

启动回扫采用独立点赞上限，最多占每日额度的 30%；每处理一屏旧动态都会返回顶部优先检查新动态，回扫超时或页面异常时自动退出回扫但不中断正常监听。

程序在启动监听时会登记当前已加载动态，并通过页面 DOM 变化持续识别真正新增的顶部动态；后来因手动滚动或回扫才加载的旧内容不会被当作新动态。空 frame 不再依赖固定页面定时器自动结束 bootstrap：宿主会持续观察非空快照，确认历史基线稳定后再显式武装；武装前分批、慢速渲染的内容全部按历史动态登记。回扫期间插入的条目先进入待判定集合，返回顶部后再根据位置判定新旧。

WebView2 脚本采用“同步启动 attempt + C# 同步 getter 轮询”的协议，不依赖 `ExecuteScriptAsync` 解包 JavaScript Promise。同步启动一旦返回 `attemptId`，同一轮不会再向其他 frame 启动点击。

每日上限和回扫上限按“已发出的点赞尝试”扣减：同步点击一旦返回有效 `attemptId` 就计入尝试额度。只有页面随后确认进入“已赞”状态，才增加成功数并写入去重集合；未确认的尝试仍占尝试额度，但不增加成功数且不去重。即使页面确认轮询异常，也不会通过不计数的方式绕过额度。

## 安全边界

- 不读取或保存 QQ 明文密码，登录输入发生在腾讯页面内。
- 登录 Cookie 仅保存在 `%LocalAppData%\QzoneLikeAssistant\WebView2`。
- 不调用私有点赞接口，不绕过验证码或风控，只点击当前页面已加载的“赞”控件。
- 默认扫描间隔 1 秒、最短操作间隔 3 秒、每日最多发出 300 次点赞尝试；可调范围以界面提示为准。
- 最后一次真实点赞尝试时间会写入本地设置；停止后重新开启或重启程序都不会清除剩余冷却时间。
- 点击停止、清除登录或关闭窗口时会立即使当前运行会话失效；已返回的旧扫描结果不会继续计数或触发下一次页面操作。
- 页面失效脚本会并发发送到主页面和全部 frame；点击动作只在同步启动脚本内执行，不存在等待若干毫秒后再自主点击的旧协程。
- 只要同步启动返回有效 `attemptId`，即使用户在脚本返回与 C# continuation 之间点击停止，该次真实尝试仍会登记额度；停止只取消后续轮询和成功处理。
- 清除登录 Cookie 不会清零当日成功数或尝试数，避免通过重新登录绕过每日上限。
- “退出并清除登录”会删除该 App 保存的 Cookie 和浏览数据。

## 构建与运行

需要 .NET 8 或更高版本的 SDK，以及 Microsoft Edge WebView2 Runtime（Windows 11 通常已自带）。

```powershell
dotnet restore
dotnet run
```

生成可直接运行的 Windows 版本：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

运行无需外部测试框架的脚本烟雾测试（需要 Node.js）：

```powershell
dotnet run --project tests/QzoneLikeAssistant.SmokeTests
```

运行真实 WebView2 集成测试（仅加载本地内存 HTML，不访问 QQ）：

```powershell
dotnet run --project tests/QzoneLikeAssistant.WebView2IntegrationTests -c Release
```

完成登录后，在右侧进入“好友动态”，再点击“开启自动点赞”。QQ 空间页面结构如果更新，需要同步调整 `LikeScript.cs` 中的按钮或动态容器选择器。
