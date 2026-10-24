# 中微子（Neutrino）— 当前上下文快照

> 产品名：中微子（Neutrino）；内部代号/代码命名空间仍为 GhostScreen。
>
> 更新：2026-10-24。给下一个工作会话用；完整技术/限制报告见 `doc/16`，细节以 `PLAN.md`、`doc/05`、`spike/*-REPORT.md` 为准。

## 1. 项目是什么

开源（Apache-2.0）Windows 11 工具：给 Agent 一块"人看不见的屏幕"。
把应用窗口停泊在物理屏之外，Agent 通过 MCP 截图/观测/点击/输入，人继续用自己的电脑。

**不是**：云桌面、RDP、RPA 平台、驱动、跨平台、商业产品。

## 2. 已定的关键技术结论

- **离屏原生窗口：PrintWindow `PW_RENDERFULLCONTENT` 是主捕获**。实测实时（FixtureApp 8/8 帧变化，p50≈12ms）。
- **WGC 只用于可见窗口/VDD**。完全离屏会冻结在最后一帧；1px 露出也无效。
- **网页：无头 Edge + CDP**（真正零窗口）。
- **Electron（VS Code）：PrintWindow 只有 chrome；必须 CDP**。
- **UWP（Calculator）：PrintWindow/UIA 完整，但启动抢焦点不稳定**；不能承诺无焦点。
- **输入没有按窗口注入的官方 API**。分层：原生 HWND 窗口消息 → CDP → 无 HWND 时的 UIA 语义动作。默认 `strict`：人在用时先返回 `USER_ACTIVE`，3 次后弹协商窗，允许/15s 倒计时后 Agent 带 `consent_id` 重试；`balanced` 直接执行 + `FocusGuard`，`force` 不等待。
- 停泊加 `WS_EX_NOACTIVATE`/`TOOLWINDOW`；**UIA Invoke/SetValue/Toggle/Select 仍会抢前台**（`WS_EX_NOACTIVATE` 挡不住程序激活），所以原生动作默认走 HWND 消息。UIA 在 strict 下由 Human Consent Gate 管理；balanced/force 下执行并由 `FocusGuard` 恢复，恢复失败返回 `FOCUS_STOLEN`。Electron/UWP 仍可能短暂抢焦点。
- 默认截图**客户区裁剪**（`IncludeFrame=false`）；坐标 = 客户区像素。

## 3. 已实现

```
src/GhostScreen.Core        Rect、CaptureMetadata、CaptureException、ProductInfo（版本）
src/GhostScreen.Windows     窗口枚举、停泊/还原、崩溃恢复还原、状态快照、FocusGuard、HumanActivity、DPI、P/Invoke
src/GhostScreen.Capture     PrintWindow（6s 超时/专用线程）、WGC、CaptureEngine（稳定性/裁剪/元数据）、PNG
src/GhostScreen.Input       UIA 观测；窗口消息 click/scroll/drag/key/set_text（HWND 子控件）
src/GhostScreen.Web         EdgeLauncher（无头 Edge/临时 profile/DevToolsActivePort）、CDP 客户端、CdpKeys、PNG 尺寸
src/GhostScreen.Daemon      ITarget/TargetManager、TargetSession（原生/消息优先）、ConsentManager、WebTargetSession、InputRouter、TargetJournal、StatusServer
src/GhostScreen.Mcp         手写 MCP stdio，21 工具（异步）+ 状态端点启动
src/GhostScreen.Cli         gs version/state/list/shot（一次性，原生）
src/GhostScreen.Tray        NotifyIcon：状态轮询/per-target 列表与关闭/打开状态页/急停/退出 + 非激活协商窗（独立小进程）
tests/FixtureApp            WinForms 夹具（按钮/输入框/列表/动画/拖拽/滚轮/品红标记）
tests/GhostScreen.Tests     零依赖 runner：32/32（原生/web MCP、焦点不变量、WPF UIA 回退、ConsentManager/严格协商门、外部 CDP、死目标、崩溃恢复、挂起降级、WGC 连续帧、DPI/DPI-unaware 修复、状态页/渲染、URL 安全）
tests/GhostScreen.WebStress 100 次 open/capture/close 泄漏压力
spike/                      P0/P1-11/P1-CLI/P1-12/P2-MCP 报告与证据
```

**MCP 工具（21）**：list_windows、attach_window、open_web、attach_cdp、list_targets、screenshot、snapshot、observe、click、click_element、scroll、drag、key、set_text、navigate、viewport、console、network、get_html、close_target、close_all。
- attach 必须 `confirm=true`；open_web 创建私有 headless Edge（无窗口）。
- attach_cdp 连接已有 Chromium/Electron 调试端口（VS Code），`external=true`，close 只 detach；外部进程死亡后 `TARGET_CLOSED`。
- 原生 observe 返回 UIA epoch + ref；web observe 返回 DOM ref/selector/bounds(CSS px)。
- web screenshot 支持 `full_page` / `clip`；`snapshot` 一次多视口并返回 `horizontal_overflow` + 越界元素；`network` 返回失败/4xx/5xx。
- 原生 click/scroll/drag/key 是客户区窗口消息；WPF/UWP 单 HWND 的坐标 `click` 自动回退到 UIA `AtPoint`；`click_element`/`set_text` 先按 UIA bounds 解析 HWND 子控件并走消息，找不到 HWND 时走 UIA（Invoke/Toggle/SelectionItem/ExpandCollapse/ValuePattern）。默认 strict：人在用时返回 `USER_ACTIVE`/`AWAITING_USER`，Agent 带 `consent_id` 重试；只有没有可执行 pattern 才返回 `ELEMENT_NOT_ACTIONABLE`。Chromium/Electron 被 InputRouter 拒绝（`INPUT_UNSUPPORTED`）。
- 原生元素动作返回 `focus: "not-needed" | "restored" | "stolen"`；`stolen` 时 MCP 以 `isError` 返回 `FOCUS_STOLEN` 警告。
- web click/scroll/drag/key 全走 CDP；坐标是 CSS 像素；截图是设备像素。
- 客户端配置见 `doc/08-MCP客户端配置.md`。

## 4. 当前验证

- `dotnet build GhostScreen.sln -c Release`：0 warning / 0 error（源码 `0.4.1-dev`）
- `dotnet run --project tests/GhostScreen.Tests -c Release`：**32/32**（WGC、WPF UIA、严格协商门测试默认 opt-in 跳过）
  - 原生 MCP 全链路；web MCP 全链路（真实 headless Edge：截图/整页/裁剪/observe/ref+坐标点击/中文输入/Ctrl+S/拖拽/滚动/console/network/390×844@2/snapshot+溢出/navigate/close）
  - **焦点安全**：`click_element`/`set_text` 走 HWND 消息（`strategy=message`），MCP 前后前台/光标/剪贴板不变（检测到人类输入时跳过该断言）；没有可执行 pattern 的元素返回 `ELEMENT_NOT_ACTIONABLE`
  - **WPF UIA 回退**（`GHOSTSCREEN_UIA_TESTS=1`）：按钮 Invoke、文本框 ValuePattern、复选框 Toggle、列表项 SelectionItem 均执行成功，`FocusGuard` 把前台恢复；Label 返回 `ELEMENT_NOT_ACTIONABLE`
  - **严格协商门**（`GHOSTSCREEN_UIA_TESTS=1`）：固定“用户活跃”探针下，前两次 `USER_ACTIVE`、第三次 `AWAITING_USER`；status 端点暴露 pending consent，允许后带 `consent_id` 执行；拒绝后 `USER_DENIED` 且不执行
  - 外部 CDP attach（连接、导航、截图、detach 不杀浏览器）；死窗口/被杀浏览器安全关闭；WGC 8 帧 ≥4 个哈希（默认 `SKIP`，需 `GHOSTSCREEN_VISIBLE_TESTS=1`）
  - 崩溃恢复：窗口还原、journal 模拟崩溃、孤儿 Edge 清理；挂起：真实 10s 挂起被 IsHungAppWindow 拒绝、attach 拒绝、2 次超时→TARGET_HUNG→快速失败
  - DPI-unaware：unaware 线程夹具，捕获尺寸 == 物理客户区，黑像素 <10%
  - WGC 可见窗口单帧；多屏负坐标布局；DPI awareness；状态端点（401/status/page/panic/清理）；状态页无头 Edge 渲染（900×700 截图，image-viewer 复核卡片/版本/急停/缩略图）
  - 测试夹具默认创建在虚拟屏之外；每个测试结束断言测试进程没有留下与虚拟屏相交的窗口
  - URL 安全、Edge 路径白名单、CdpKeys、PNG 单元测试
- `dotnet run --project tests/GhostScreen.WebStress -c Release -- --cycles 100`：**0 失败**；托管内存约 0.9MB、私有 32–38MB、句柄 362 封顶、profiles 0
- `dotnet run --project tests/GhostScreen.Soak -c Release -- --minutes 3 --interval 5`：**PASS**；单目标 29 轮，预热后句柄 +6、私有 +2MB、profiles 0；`--native 2 --web 2` 24 轮/48 web 目标 0 失败、句柄 +9、私有 -4MB；5 分钟 4 目标 39 轮/78 web 目标 0 失败、句柄 +1、私有 -3MB
- 官方 MCP SDK 客户端（`@modelcontextprotocol/sdk` 1.32.2 / Node 22）stdio 联调：initialize、20 工具、open_web、PNG image、observe、close_all 全部通过
- 真实 VS Code 1.142：`attach_cdp { port: 9223, page_url: "workbench" }` → 115KB PNG、observe 30 元素、detach 后 VS Code 存活
- 安装脚本：`scripts/install.ps1` 安装/开始菜单快捷方式/卸载往返通过（unsigned）
- 干净目录自包含冒烟：把 0.3.0-beta 复制到 `%TEMP%`，PATH 只留 `C:\Windows\System32`（无 dotnet），CLI/MCP 仍启动、20 工具、open_web/close_all 通过；真实无 .NET 新机器仍未验证
- P1-12 安静重测：`humanInput=false`，前台/光标/剪贴板不变，3 帧哈希不同，`pass=true`
- `dotnet format --verify-no-changes`：通过
- 0.4.0-beta 包：`artifacts/GhostScreen-0.4.0-beta-win-x64.zip`（172 MB，SHA256 `1A87DC63...FCBC225`）；发布版冒烟（21 工具 + open_web + close_all + 状态页 + tray）
- 关闭 web target 后 Edge 进程退出、`%LOCALAPPDATA%\GhostScreen\web-profiles` 为空
- 状态端点：MCP 启动后 stderr 打印 URL；`%LOCALAPPDATA%\GhostScreen\status.json` 含 url/token/port/pid，退出删除；状态页含 target 卡片与 5s 刷新缩略图（`/api/screenshot`）
- 本地提交 `fc06d29` + `ac352f3`；GitHub 远端待 `gh auth login` 后创建

## 5. 已知限制

- 原生元素动作只对 **HWND 子控件**能保证零激活；WPF/UWP/自绘控件没有子 HWND，UIA 动作会短暂激活目标。strict 默认通过协商门控制；`balanced`/`force` 仍可能短暂打断。
- strict 的协商窗依赖托盘进程；托盘未运行时服务器仍会在 15s 后自动放行，但没有可视提示。多 Agent 排队、允许 5 分钟、锁屏处理仍待做。
- 键盘是消息级，复杂 IME/快捷键需应用级验证。
- 没有独立 daemon/HTTP/多客户端；MCP 单进程；崩溃后靠下次启动恢复（已实现），独立 watchdog 未做。
- 状态页跟随 MCP 进程，MCP 退出/崩溃即失效；缩略图 0.2fps（无连续帧/SSE）；token 在 URL 中，仅本机使用。
- 托盘是独立轮询进程：有 per-target 列表/单目标关闭/打开状态页/急停/退出；没有暂停恢复/自启/降级图标区分。
- Crash journal 是单进程假设；两个 MCP 实例共享会互相清理。
- Web：整页受 16384px 限制；clip 只接受坐标；network 不含请求/响应体；跨 iframe 覆盖有限；移动端超宽内容会触发 shrink-to-fit。
- 与有头 Edge 的像素 diff 未做；只发现 Edge，无 Chrome 回退。
- 没有 TTL/配额/cancellation；官方 MCP SDK 客户端已联调，但干净机器/无 .NET 环境未验证。
- DPI-unaware 捕获已修复（单屏 150%：unaware 线程上下文 + 物理缩放）；混合 DPI/双屏实测未做（负坐标布局有单测）；VDD 未测。
- P1-12 已在安静环境通过；per-target worker 已决定不做（超时线程由降级封顶 ≤2）；WGC 可见单帧已验证，VDD/连续帧未做。
- 24h soak 未跑满；4 target/混沌未做。
- spike 的测试启动器用固定常量 `CreateProcessW`（无 shell/无外部输入；测试专用）。

## 6. 下一步顺序

1. 真实 UWP 夹具验证 UIA 回退 + 协商门；WPF 已验证（Invoke/Toggle/SelectionItem/ValuePattern 均执行并恢复前台）。
2. 24h soak（`--minutes 1440`）+ 4 target/混沌（杀进程、最小化、切桌面、断网）。
3. P4-04 硬件受限项：第二块屏/混合 DPI 实测、锁屏/唤醒；DPI-unaware 已修复单屏。
4. 托盘 per-target 列表/暂停恢复；状态页 2–5fps（按需）。
5. 干净机器解压验证；VDD/连续帧；签名/安装器。
6. 之后：独立 daemon/多客户端。

## 7. 工作守则

- 先读本文件 + `PLAN.md` + `doc/05` 最后一条；需要全貌读 `doc/16`。
- 一次一个垂直切片；收工 build/test/format 绿。
- 不把黑图/陈旧帧当成功；不抢前台/光标；不伪造测试通过。
- 新决策写 `doc/05`，范围/验收写 `PLAN.md`。
