# P2 MCP / TargetManager 基础切片

> 日期：2026-10-24
> 目标：让 Agent 通过 MCP 完成 attach → screenshot → observe → click/set_text → close，并在断连时还原窗口。
> 证据：`spike/evidence/tests.log`、`mcp-shot.png`、`mcp-stdio.log`。

## 1. 组件

- `GhostScreen.Daemon.TargetManager` / `TargetSession`
  - 持有 `ParkedWindow`，跨工具调用保持 target。
  - 每个 target 用锁串行化 capture/observe/input。
  - `Close` / `CloseAll` 还原；MCP EOF 时自动 `CloseAll`。
- `GhostScreen.Mcp.McpServer`
  - 手写 JSON-RPC 2.0 stdio（无外部 SDK 依赖）。
  - `initialize` / `ping` / `tools/list` / `tools/call`。
  - `initialize` 回显客户端 protocolVersion；支持 `2024-11-05`。
  - stdout 只输出 JSON-RPC；日志走 stderr。
- `CaptureEngine` 默认输出**客户区裁剪**（`IncludeFrame=false`），元数据 `clientOrigin=(0,0)`；需要标题栏时传 `IncludeFrame=true`。

## 2. MCP 工具

| 工具 | 作用 |
|---|---|
| `list_windows` | 列可见窗口（可含 invisible/own process） |
| `attach_window` | 按 hwnd/title 停泊，需 `confirm=true` |
| `list_targets` | 当前 target |
| `screenshot` | PNG image content + metadata |
| `observe` | UIA 元素树（epoch + role/name/automation_id/bounds/patterns） |
| `click` | 按客户区坐标点击（窗口消息） |
| `click_element` | 按 `ref`（最新 observe）或 UIA name Invoke，不移动光标 |
| `scroll` | 客户区坐标滚轮（窗口消息） |
| `drag` | 客户区起止点拖拽（窗口消息） |
| `key` | 按键/热键（窗口消息，如 `["CTRL","S"]`） |
| `set_text` | 按 AutomationId SetValue |
| `close_target` | 还原单个 target |
| `close_all` | 还原全部（panic） |

## 3. 测试结果

- 进程内 MCP JSON-RPC 集成测试：**8/8 passed**（需要交互式桌面；托管 CI/Session 0 会 SKIP，GUI 集成走 self-hosted runner）。
  - initialize / tools/list
  - list_windows 包含 fixture 标题
  - attach_window（confirm=true）→ target_id
  - screenshot → PNG（签名有效、非空、PrintWindow）
  - observe → epoch + `Click Me` / `txtInput`
  - click_element by **ref** → fixture `status: clicked 1`
  - click by coordinate (420,33) → fixture `status: clicked 2`（窗口消息）
  - scroll 在滚轮区域 → fixture `wheel 120`
  - drag (380,270)→(500,300) → fixture `drag done`
  - key `["F5"]` → fixture `key F5`
  - InputRouter 对 `msedge` 返回 `INPUT_UNSUPPORTED`（单测）
  - set_text → fixture 输入框 `mcp 世界`
  - close_target → ok
- 真实 stdio 管道：`initialize` + `tools/list` 正常，EOF 后打印 "all targets restored"。
- MCP 截图：**680×420 客户区**，完整夹具 UI，无窗口边框黑边。
- 清理：测试结束后 fixture 关闭；无遗留 target/窗口。

## 4. 限制

- MCP 目前是**单进程**：`attach` 后由该进程持有 target；没有独立 daemon/HTTP，多个 Agent 客户端不能共享 target。
- 只支持原生窗口 + UIA；Web/CDP target 还没有接进 MCP。
- `click_element`/`set_text` 覆盖 UIA 可用控件；`click`/`scroll`/`drag`/`key` 是客户区窗口消息，Chromium/Electron 会被 `InputRouter` 拒绝。
- 键盘是消息级，依赖应用处理 `WM_KEYDOWN`；复杂 IME/快捷键仍需应用级验证。
- 没有 target TTL/配额；没有 cancellation；真实 MCP 客户端联调待做。
- 崩溃（非 EOF）不会还原；需要后续 watchdog/持久化。
- `attach_window` 会移动窗口；默认要求 `confirm=true`。

## 5. 下一步

1. P2-01/02/03/04：UIA epoch/ref、窗口消息、坐标点击、InputRouter、capabilities。
2. P2-09：MCP 客户端配置文档 + 在真实客户端（ZCode/Claude/Cursor）联调。
3. P2-10：v0.1.0-alpha（zip/SHA256/CHANGELOG）。
4. 独立 daemon/HTTP + 多客户端（P2/P4）。
5. Web target 接 MCP（P3）。
