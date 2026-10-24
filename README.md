# 中微子（Neutrino）

> 产品名：**中微子（Neutrino）**。内部代号、仓库目录、可执行文件和 MCP server key 暂时仍为 `GhostScreen`/`ghostscreen`，避免破坏已有配置；技术重命名单独进行。

给 Agent 一块**人类看不见的屏幕**。

应用窗口在系统里照常真实渲染，但不出现在人的显示器上。Agent 可以随时截图、点击、输入；人继续自己的工作，不闪窗、不被抢焦点、不被抢鼠标。

> 状态：**0.4.1-dev**（基于已发布的 0.4.0-beta；焦点安全修复与 WPF UIA 验证，未打包）。含崩溃恢复、挂起拒绝、DPI-unaware 修复、状态页/托盘（per-target 关闭）、`attach_cdp`（VS Code/Electron）、soak harness 与 unsigned 安装脚本；MCP **21 工具**，测试 **29/29**。产品仍早期。
> 关键实测：WGC 对完全离屏窗口会冻结；PrintWindow 离屏实时（p50≈12ms）。架构已改为 **PrintWindow 主捕获 + WGC 可见/VDD + CDP 网页**。
> 定位：**开源项目（Apache-2.0）**，不做商业产品；工程质量、兼容性、可维护性优先。执行计划见 [07-开源项目规划](doc/07-开源项目规划.md)。
> 工作名可改，不影响设计。

## 当前进展

- ✅ **P0**：停泊到虚拟屏之外；物理屏像素验证无窗口痕迹；前台/剪贴板不变量通过。
- ✅ **P0**：PrintWindow 离屏实时：FixtureApp 8/8 帧变化（帧号 39→49），p50≈12ms；Notepad 完整捕获并写入中文。
- ✅ **P0**：WGC 仅可见时实时（10/10 帧）；离屏 1/20（冻结），1px 露出无效。
- ✅ **P1/P2**：原生元素动作**消息优先**（HWND 子控件 `BM_CLICK`/`WM_SETTEXT`）；没有 HWND 的 WPF/UWP 控件自动走 UIA（Invoke/Toggle/SelectionItem/ExpandCollapse）并恢复前台；只有控件完全没有可执行 pattern 时才返回 `ELEMENT_NOT_ACTIONABLE`。
- ✅ **P4（0.4.1-dev）**：焦点安全修复——UIA 动作即使窗口带 `WS_EX_NOACTIVATE` 也会短暂激活；默认执行并由 `FocusGuard` 恢复前台，`strict_no_focus_steal=true` 可选择拒绝；测试夹具默认屏外创建。
- ✅ **P2**：TargetManager + MCP stdio（20 工具）；截图默认客户区裁剪；EOF 自动还原；in-process 测试。
- ✅ **P3**：`open_web` 启动私有**无头 Edge**（无窗口、独立临时 profile）；截图/observe/点击/输入/按键/滚动/拖拽/console/视口/导航全部经 CDP；关 target 即杀进程并删 profile。
- ✅ **P3**：整页截图与 clip、network idle、多视口 `snapshot`（横向溢出 + 越界元素定位）、`network` 失败诊断；100 次开关 0 失败、句柄/内存封顶、profile 0。
- ✅ **P4（0.3.0-beta）**：崩溃恢复——park/open_web 记账到 `targets.json`，下次启动校验 hwnd/pid/类名后还原窗口；孤儿 Edge 按 pid+启动时间清理并删 profile；损坏 journal 不阻塞启动。挂起窗口用 `IsHungAppWindow` 直接拒绝，不把旧帧当成功。
- ✅ **P4（0.3.0-beta）**：本地状态页——MCP 启动后在 stderr 打印 `http://127.0.0.1:<port>/?token=…`，可查看 target 卡片/状态/恢复摘要/5 秒刷新缩略图，并一键急停；token 必填，端点信息写入 `%LOCALAPPDATA%\GhostScreen\status.json`，退出删除。
- ✅ **P4（0.3.0-beta）**：托盘 `GhostScreen.Tray.exe`——独立小进程，每 2 秒轮询状态端点，显示版本/target 数；菜单可打开状态页、急停、退出；MCP 退出后显示“未运行”。
- ✅ **P4（0.4.0-beta）**：DPI-unaware 目标的 PrintWindow 黑边修复——在不感知 DPI 的线程上下文捕获，再按物理尺寸缩放；1020×630 截图内容铺满、黑像素 <10%，有自动测试。
- ✅ **P4（0.4.0-beta）**：托盘 per-target 列表与单目标关闭（`/api/close_target`）；unsigned 安装脚本 `scripts/install.ps1`（复制到 LocalAppData + 开始菜单快捷方式 + 卸载）。
- ✅ **P4（0.3.1-beta）**：`attach_cdp` 连接已有 Chromium/Electron DevTools 端口（VS Code/Electron），关闭 target 只 detach、不杀外部浏览器；浏览器被杀后操作返回 `TARGET_CLOSED`，死 target 仍可安全关闭。
- ✅ **P4（0.3.0-beta）**：soak harness（`tests/GhostScreen.Soak`）单目标 3 分钟与 4 目标 3 分钟预跑均 0 失败；预热后句柄 +6/+9、私有内存 +2/-4MB、profile 0；24h 留给 CI/后台。
- ⚠️ **待办**：多屏/混合 DPI、24h soak、headed/headless 像素 diff、干净机器验证；WGC VDD/连续帧；per-target worker 已决定不做，P1-12 安静重测已通过。

## P1-11 现代应用结果

- **Edge GUI（Chromium）**：PrintWindow 完整拿到浏览器 chrome + 页面内容；UIA 可用；非激活启动。
- **VS Code（Electron）**：PrintWindow 只有窗口 chrome；**CDP 完整**（workbench 截图/DOM/输入）；需调试端口。
- **Calculator（UWP）**：PrintWindow + UIA 完整；**焦点隔离不稳定**（多次运行会抢前台），需显式同意/独立会话。
- 报告：[spike/P1-11-REPORT.md](spike/P1-11-REPORT.md)。

## P1-03 / P1-12 进展

- PrintWindow 6s 超时已加入，跑在专用后台线程，避免挂起应用拖垮线程池；per-target worker 复用仍待做。
- `--quiet` 不变量测试已实现；2026-10-24 安静环境复测 `humanInput=false`，前台/光标/剪贴板不变，3 帧哈希不同，`pass=true`。
- 报告：[P1-12](spike/P1-12-REPORT.md)。

## CLI（P1 基础切片）

```bash
dotnet build src/GhostScreen.Cli -c Release
./src/GhostScreen.Cli/bin/Release/net8.0-windows10.0.19041.0/GhostScreen.Cli.exe list
./src/GhostScreen.Cli/bin/Release/net8.0-windows10.0.19041.0/GhostScreen.Cli.exe shot --hwnd 0x1234 --confirm --out shot.png
```

`shot` 会先停泊、截图、再还原；移动别人的窗口必须显式 `--confirm`。报告：[P1-CLI](spike/P1-CLI-REPORT.md)。

## MCP（P2/P3 切片）

```json
{
  "mcpServers": {
    "ghostscreen": {
      "command": "E:\\software_code\\Visual_Stdio_Code\\WindowsForAgent\\src\\GhostScreen.Mcp\\bin\\Release\\net8.0-windows10.0.19041.0\\GhostScreen.Mcp.exe",
      "args": []
    }
  }
}
```

> MCP server key 暂保留 `ghostscreen`（内部代号），产品展示名为“中微子（Neutrino）”；改名会破坏已有客户端配置，留到技术重命名阶段。

**原生窗口**：`list_windows → attach_window (confirm=true) → screenshot / observe / click / click_element / scroll / drag / key / set_text → close_target / close_all`。
截图返回 PNG image content + 元数据；默认客户区裁剪；EOF 自动还原所有窗口。

**网页**：`open_web { url, width, height, device_scale_factor }`（或 `attach_cdp { port }` 连接已有 Edge/Electron 调试端口）`→ screenshot (可 full_page/clip) / snapshot / observe / click / click_element / set_text / key / scroll / drag → navigate / viewport / console / network / get_html → close_target`。

- `attach_cdp` 不启动也不关闭外部浏览器：`close_target` 只 detach；适合 VS Code 这类必须用 CDP 的 Electron 应用。
- `open_web` 不弹窗、不碰你的浏览器配置：独立临时 profile，退出即删。
- `snapshot` 一次拍多个视口并给出 `horizontal_overflow` 与越界元素 selector，适合响应式验收。
- `screenshot { full_page: true }` 截整页；`clip` 按 CSS 像素裁剪。截图后自动恢复视口。
- 点击坐标用 **CSS 像素**；截图是设备像素（`image = css × device_scale_factor`），元数据同时给出 `css_width/css_height`。
- 中文输入与 `CTRL+S` 等组合键已实测；`set_text` 走 React 兼容的 native setter + input/change。
- 关闭 target 后 Edge 进程退出、profile 目录清零；100 次开关压力测试通过。

报告：[P2-MCP](spike/P2-MCP-REPORT.md)；完整工具与坐标约定见 [03-接口设计](doc/03-接口设计.md)。

## 开发与测试

```bash
dotnet build GhostScreen.sln -c Release
dotnet run --project tests/GhostScreen.Tests -c Release
dotnet format GhostScreen.sln --verify-no-changes
```

- 集成测试会真实启动 WinForms 夹具、WPF 夹具、UIA、无头 Edge；夹具默认创建在虚拟屏之外，不会在你的桌面闪窗。
- WGC 必须捕获可见窗口，因此那条测试默认跳过；显式跑会短暂显示一个窗口：
  `GHOSTSCREEN_VISIBLE_TESTS=1 dotnet run --project tests/GhostScreen.Tests -c Release`
- WPF 的 UIA 回退必须短暂激活目标窗口（随后恢复前台），因此那条测试也默认跳过；显式跑会短暂改变前台焦点：
  `GHOSTSCREEN_UIA_TESTS=1 dotnet run --project tests/GhostScreen.Tests -c Release`
- 测试结束会断言：测试进程没有留下任何与虚拟屏相交的窗口。

## 发布包（最新：0.4.0-beta；源码 0.4.1-dev 未打包）

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

生成 `artifacts/GhostScreen-0.4.0-beta-win-x64.zip`（约 172 MB，self-contained MCP/CLI/Tray）。版本号来自 `src/GhostScreen.Core/ProductInfo.cs`；SHA256 见同名 `.sha256` 文件。
当前源码版本已是 `0.4.1-dev`（焦点安全修复）；重新打包会生成 `GhostScreen-0.4.1-dev-win-x64.zip`，不会覆盖 0.4.0-beta 的发布包。

安装（可选，unsigned）：`powershell -ExecutionPolicy Bypass -File scripts/install.ps1`
卸载：`powershell -ExecutionPolicy Bypass -File scripts/install.ps1 -Uninstall`

说明：[0.4.0 beta 发布说明](doc/17-0.4.0-beta发布说明.md)；启动配方见 [doc/12-Recipes](doc/12-Recipes.md)。

完整数据与截图：[spike/REPORT.md](spike/REPORT.md) 与 `spike/evidence/`。

## 它解决什么

- 前端 Agent 做视觉验证时，浏览器窗口不再弹到人眼前。
- Agent 的点击默认不移动真实光标；HWND 控件不抢前台；没有 HWND 的 WPF/UWP 控件会短暂激活目标并自动恢复前台。需要绝对零激活时用 `strict_no_focus_steal=true` 拒绝。
- 多个 Agent 可以各自持有 target 并行工作，人类屏幕保持干净。
- 不再需要给每个 Agent 配一台显示器或虚拟机。

## 核心思路

1. **停泊**：把 Agent 的窗口移到所有物理显示器之外，但保持"显示/非最小化"状态，让 DWM 继续合成它。
2. **分层捕获**：离屏原生窗口用 PrintWindow（实测实时）；可见/VDD 用 WGC；网页目标直接用无头 Edge + CDP（真正零窗口）。
3. **分层输入（执行优先，拒绝兜底）**：原生 HWND 窗口消息 → CDP → 无 HWND 时的 UIA 语义动作（Invoke/Toggle/SelectionItem/ExpandCollapse/ValuePattern，执行后自动恢复前台）→ 最后才是有侵入性的 SendInput（未实现，需显式授权）。
4. **统一接口**：MCP 工具 + 本地 HTTP/WS；每个 target 上报自己的能力与降级原因。
5. **人类优先**：不抢焦点、不动光标、不碰剪贴板；有托盘指示、审计日志与一键急停。

## 开源与质量

- **Apache-2.0**，无遥测、无账号、无付费墙、无云依赖。
- 质量红线：不把黑图当成功；不抢焦点/光标/剪贴板；只承诺支持矩阵内的应用；每个版本有测试、校验和、CHANGELOG。
- v1.0 目标：6–8 个应用做扎实、5 分钟 Quick Start、MCP 即插即用、24h soak。
- 完整规划（范围、质量门槛、路线图、发布与社区）：[07-开源项目规划](doc/07-开源项目规划.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [PLAN.md](PLAN.md) | 长程执行看板：P0–P5、WBS、Gate、风险、会话协议 |
| [16-技术实现与限制报告](doc/16-技术实现与限制报告.md) | 完整技术/实现逻辑、实测结论、验证证据、无法完成项与所需支持 |
| [00-项目概览](doc/00-项目概览.md) | 痛点、目标、场景、成功标准、术语 |
| [01-技术可行性与方案选型](doc/01-技术可行性与方案选型.md) | 捕获/输入方案对比、关键假设、已知限制、参考来源 |
| [02-架构设计](doc/02-架构设计.md) | 进程模型、target 生命周期、停泊/捕获/输入/安全设计、ADR |
| [03-接口设计](doc/03-接口设计.md) | MCP 工具、HTTP/WS、CLI、错误码、坐标约定 |
| [04-路线图与验收](doc/04-路线图与验收.md) | M0–M8、测试矩阵、DoD、风险 |
| [05-进度与决策日志](doc/05-进度与决策日志.md) | 跨会话接力记录 |
| [06-市场与需求调研](doc/06-市场与需求调研.md) | 竞品、需求证据、市场规模、收益模型、定位建议 |
| [07-开源项目规划](doc/07-开源项目规划.md) | 开源定位、质量红线、范围、工程/测试/发布/社区规划 |
| [spike/REPORT.md](spike/REPORT.md) | P0 实测报告：WGC/PrintWindow/UIA/CDP、架构修订、证据 |
| [spike/P1-11-REPORT.md](spike/P1-11-REPORT.md) | 现代应用兼容性：Edge GUI / VS Code / Calculator |
| [spike/P1-12-REPORT.md](spike/P1-12-REPORT.md) | 安静环境不变量测试与污染结果 |
| [spike/P1-CLI-REPORT.md](spike/P1-CLI-REPORT.md) | CaptureEngine + CLI（list/state/shot）与 DPI 发现 |
| [spike/P2-MCP-REPORT.md](spike/P2-MCP-REPORT.md) | TargetManager + MCP stdio（13 工具）与集成测试 |
| [08-MCP客户端配置](doc/08-MCP客户端配置.md) | Claude Code / Codex / Cursor / ZCode 配置与工作流 |
| [09-0.1.0 alpha 发布说明](doc/09-0.1.0-alpha发布说明.md) | 上一版发布包、SHA256、限制 |
| [10-0.2.0 alpha 发布说明](doc/10-0.2.0-alpha发布说明.md) | 历史：网页目标首版 |
| [11-0.2.1 alpha 发布说明](doc/11-0.2.1-alpha发布说明.md) | 上一版：整页/裁剪、snapshot、network、100 次压力 |
| [12-Recipes](doc/12-Recipes.md) | 应用/网页启动配方、兼容矩阵、安全边界 |
| [13-0.2.2 beta 发布说明](doc/13-0.2.2-beta发布说明.md) | 上一版：G3 验收、beta 范围 |
| [14-0.3.0 beta 发布说明](doc/14-0.3.0-beta发布说明.md) | 上一版：崩溃恢复、状态页、托盘、soak |
| [15-0.3.1 beta 发布说明](doc/15-0.3.1-beta发布说明.md) | 上一版：attach_cdp、死目标保护、WGC 连续帧 |
| [17-0.4.0 beta 发布说明](doc/17-0.4.0-beta发布说明.md) | 本版：DPI-unaware 修复、托盘 per-target、VS Code 实测、安装脚本 |

## 技术栈（拟）

- .NET 8（Win32/WinRT/UIA/COM 互操作）
- PrintWindow `PW_RENDERFULLCONTENT`（离屏主捕获）
- Windows.Graphics.Capture + Direct3D11（可见/VDD 连续帧）
- 无头 Edge + Chrome DevTools Protocol（网页）
- ASP.NET Core 最小 API + WebSocket（本地服务）
- MCP（stdio）+ 极简本地状态页
- 可选：IddCx 虚拟显示器（检测用户自装驱动）

## 平台

Windows 11（当前在 26200 上设计）。Windows 10 2004+ 理论上可用，但非 v1 目标。

## License

Apache-2.0。
