# P1-11 现代应用兼容性报告

> 日期：2026-10-24
> 目标：验证 PrintWindow / WGC / UIA / CDP 对现代应用的可用性，决定捕获后端与支持级别。
> 证据：`spike/evidence/modern-*`、`run-modern.log`、`modern.json`。

## 1. 结果总表

| 应用 | 启动/停泊 | PrintWindow | WGC（离屏） | UIA | 焦点 | 结论 |
|---|---|---|---|---|---|---|
| **Edge GUI**（Chromium） | 通过；`--window-position=2700,200` 直接屏幕外 | ✅ **完整**：浏览器 chrome + 本地页面（`GhostScreen Spike Page`、`Click Me`、`frame 10`），1800×1350 | 非黑（但离屏会冻结） | 19 个元素 | ✅ 未抢前台 | **支持**（捕获 + UIA；输入优先 CDP） |
| **VS Code**（Electron） | 通过；`SW_SHOWMINNOACTIVE` 非激活启动 | ❌ 只有窗口 chrome（三个标题按钮），客户区纯深灰 | 非黑但不可靠 | 4 个元素 | ⚠️ 通常不抢；多次运行中出现抢前台案例 | **PrintWindow 不支持；CDP 支持**（`document.title=Visual Studio Code`，完整工作台截图） |
| **Calculator**（UWP/WinUI） | 通过；最小化非激活启动 | ✅ **完整**：科学计算器键盘 + 显示 `0` + 标题 `计算器`，502×810 | 非黑（离屏冻结） | 50 个元素 | ⚠️ 不稳定：多次抢前台，`SetForegroundWindow`/`AttachThreadInput`/`SwitchToThisWindow` 恢复失败或偶发成功 | **捕获/UIA 支持；焦点隔离不保证** |

## 2. 关键结论

1. **PrintWindow 不是"Chromium 一定黑"**。Edge GUI 的页面 + chrome 完整渲染，说明在窗口已经绘制过的情况下，PrintWindow 能拿到 Chromium 的合成结果。此前"Chromium 全黑"的担心不成立（至少 Edge 156 上）。
2. **Electron 不能依赖 PrintWindow**。VS Code 只拿到窗口 chrome，应用 UI 完全缺失；必须用 CDP（或 VDD/WGC 的可见路径）。VS Code 在带 `--remote-debugging-port` 时 CDP 完整可用。
3. **UWP 的 PrintWindow 可用**。Calculator 的科学模式完整；UIA 50 个元素。但 UWP 启动会激活自身，当前的三条前台恢复路径都失败。
4. **非激活启动只能降低、不能消除焦点风险**：
   - Edge GUI：`--window-position` + `SW_SHOWNOACTIVATE`，多次运行前台稳定不变。
   - VS Code：`SW_SHOWMINNOACTIVE` 通常不抢，但多次运行中出现抢前台。
   - Calculator：`SW_SHOWMINNOACTIVE` 仍多次被 UWP 激活；恢复 API 偶发成功，不保证。

## 3. 对产品的直接影响

- **后端选择**：
  - 网页目标：继续**无头 Edge + CDP**（首选）。
  - 原生 Chromium（Edge GUI）：PrintWindow 捕获可用；如果由我们启动，优先 CDP 以获得 DOM/输入。
  - Electron（VS Code 等）：**PrintWindow 不保证**；由我们启动时加 `--remote-debugging-port` 并用 CDP；否则标记"部分支持/需要可见窗口或 VDD"。
  - UWP/WinUI：PrintWindow 捕获可用；焦点隔离未解决。
- **焦点策略**：
  - agent 启动的窗口用 `SW_SHOWMINNOACTIVE`/`SW_SHOWNOACTIVATE` + `WS_EX_NOACTIVATE` + 屏幕外位置，避免激活。
  - UWP 默认不承诺"不抢焦点"；要么显式征得用户同意，要么用独立会话/虚拟显示器（未来）。
  - `ForegroundGuard` 对普通 Win32/WinForms/Notepad 有效；对 UWP 不可靠。
- **支持矩阵**（更新到 07）：
  - Edge GUI：✅ 捕获（PrintWindow）+ UIA；输入优先 CDP。
  - VS Code：🟡 PrintWindow 不支持；CDP ✅；启动需带调试端口。
  - Calculator：🟡 捕获/UIA ✅；焦点 ❌。

## 4. 证据

| 文件 | 内容 |
|---|---|
| `modern-EdgeGui-printwindow.png` | Edge chrome + 本地页面完整渲染（frame 10） |
| `modern-VsCode-printwindow.png` | 只有窗口 chrome，客户区纯深灰 |
| `modern-VsCode-cdp.png` | CDP 完整 VS Code 工作台（菜单/活动栏/欢迎页/Chat 面板） |
| `modern-Calculator-printwindow.png` | 完整科学计算器 UI |
| `modern.json` | 三款应用的启动/停泊/捕获/UIA/焦点/清理结果 |
| `run-modern.log` | 运行日志 |

## 5. 清理

- 三款测试窗口均关闭；Edge GUI/VS Code/Calculator 无残留。
- 测试 Edge profile 进程数 0；VS Code profile 已删除。
- 测试期间前台被 Calculator 抢过一次；已记录，未影响用户数据。

## 6. 下一步

1. **P1-03（部分完成）**：PrintWindow 6s 超时 wrapper 已加入；per-target worker 隔离/挂起降级仍待做。
2. **P1-12**：安静环境重测光标/焦点；UWP 焦点问题单独记录为已知限制。
3. **P1-01/02**：窗口注册表、停泊生产化；把 `SW_SHOWMINNOACTIVE`/`--window-position` 变成受控启动 profile。
4. 输入验证：Edge CDP 点击、VS Code CDP 输入、Calculator UIA 按键（P2）。
