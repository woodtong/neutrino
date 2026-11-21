# 功能 TODO 与可行性记录

> 本文件收录**尚未排期**的候选功能与预研结论。正式排期后移入 `PLAN.md` WBS，并在对应条目标注去向；已完成的条目保留结论供回溯。

---

## 1. 提示/通知功能（2026-11-20 预研，可行性 ✅）

**目标场景**（2026-11-20 用户提出：提醒用户松手操作、进程问题等情况）：

1. **松手提醒**：agent 动作被 `USER_ACTIVE` 阻塞（人正在用鼠标/键盘，严格模式拒绝抢焦点），人完全不知道 agent 在等自己 → toast「agent 正在等你松手」。
2. **进程/目标问题**：`TARGET_HUNG`（目标挂起）、`WINDOW_CLOSE_REFUSED`（agent-only 窗口拒绝关闭、已被兜底还原到桌面）、启动时 journal 崩溃恢复（还原了 N 个窗口/清了 M 个孤儿 Edge）等需要人知晓的事件。

### 可行性结论（已验证）

- **落点：GhostScreen.Tray**。Tray 已常驻、已持有 `NotifyIcon`、已每 2s 轮询 `/api/status`（`src/GhostScreen.Tray/Program.cs` TrayContext.RefreshAsync）、已有 `ConsentPromptForm` 非激活协商窗先例 → 复用 `NotifyIcon.ShowBalloonTip`，**零新依赖**。
- **Spike 证据（2026-11-20，本机）**：PowerShell 瞬态宿主 `NotifyIcon.ShowBalloonTip` 连弹 3 次，用户确认右下角出现 toast 横幅——Win11 将气泡渲染为 toast，无需 AUMID 注册、无需打包、无需 WinRT/AppNotification 包。
- **延迟**：≤ 轮询间隔 2s，对「提醒松手」类场景足够。

### 设计草案（排期时细化）

- **「进程问题」类：纯 Tray 端差分即可**。`/api/status` 快照已含 `targets`（state / degraded_reason / visibility / zone）、`consents`、`recovery`（StatusServer.BuildStatusJson）——Tray 记住上一次快照，对状态跃迁（如 state→degraded、recovery.restored>0）弹 toast。
- **「松手提醒」需服务端补一个信号**：`TargetSession` 记录最近一次 user-active 阻塞（时间戳 + 动作描述），经 `ToDto()` 或 `/api/status` 暴露；Tray 观察到同一 target 连续 K 次轮询（≈4–6s）处于 user-active 阻塞时弹「agent 正在等你松手（target 标题）」。
- **节流（关键）**：按 `(target, 事件类型)` 去重 + 冷却窗口（如 60s），避免 USER_ACTIVE 持续期间每 2s 重弹；原则=只弹「需要人决策/知晓」的事件，agent 可自恢复的错误重试不弹。
- **降级**：系统勿扰/专注助手抑制 toast 时 → 回退为托盘 Tooltip 更新/图标状态（现有机制），不额外造轮子。
- **初版事件清单**：`user-active-waiting`、`target-degraded`（TARGET_HUNG）、`close-refused-restored`（窗口已还原到桌面，请人工处置）、`recovery-restored`（启动恢复摘要）、`consent-pending`（已有协商窗，toast 仅作远端补充）。

### 风险

- 通知风暴：依赖节流规则，初版宁缺勿滥。
- toast 文案语言：跟随 Tray 现状用中文。

**建议排期**：P4-13，估 0.5 天（服务端 user-active 信号 + Tray 差分与节流 + 测试）。

### 落地状态（2026-11-21，0.6.0-dev）✅

已实现，但与草案有两处不同，记在这里免得起草时的假设被当成现状：

- **不只是 Tray 端差分**：通知抽成了独立模块 `GhostScreen.Notifier`（命名管道 `GhostScreen.Notifier`，一行 JSON 请求 / 一行 JSON 回复）。托盘在跑时由托盘宿主管道服务，托盘不在时调用方按需拉起 exe——否则"MCP 自己挂了"这一类事件永远报不出来。
- **「松手提醒」不需要差分轮询**：`TargetSession` 在第二次被 `USER_ACTIVE` 挡住时直接发 `user-active:{target}` toast（`NotifierClient` 按 key 60s 冷却），比"连续 K 次轮询"更早也更准。
- 事件名最终为：`user-active:{target}`、`consent:{id}`、`degraded:{target}`、`close-refused:{target}`、`recovery-restored`、`mcp-down`、`mcp-fatal`；文案集中在 Core 的 `ConsentPromptCopy` 与各调用点，两条宿主路径一致。
- 细节见 `doc/02-架构设计.md` §8.1.2、`doc/03-接口设计.md` §4.2。

---

## 2. 其他候选（占位）

*（待补充；格式建议同上：场景 → 可行性 → 设计草案 → 建议）*
