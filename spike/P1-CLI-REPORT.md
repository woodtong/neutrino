# P1 CLI / CaptureEngine 报告

> 日期：2026-10-24
> 目标：把 spike 的捕获/停泊能力收成可复用产品代码，提供一个可验证的 CLI 垂直切片。
> 证据：`spike/evidence/cli-shot.png`、`cli-shot.json`、`cli-state-before/after.json`。

## 1. 实现范围

- `GhostScreen.Core`：`CaptureMetadata`、`CaptureException`（结构化 code）。
- `GhostScreen.Capture`：
  - `CaptureEngine`：PrintWindow 主捕获、可见窗口 WGC 兜底、稳定性等待、元数据（窗口/客户区/客户区原点/DPI）。
  - `PrintWindowCapture.TryCaptureWithTimeout`：6s 超时，专用后台线程。
- `GhostScreen.Cli`：
  - `gs version`
  - `gs state`（显示器、虚拟屏、前台、光标、剪贴板、lastInput）
  - `gs list [--all] [--json]`（默认过滤 tool window / 空标题）
  - `gs shot --hwnd 0x... --out path --confirm [--no-stable]`

## 2. 验证结果

```text
ghostscreen 0.1.0-p0
shot:
  backend=printwindow
  width=680 height=420
  blank=false
  dpi=150
  clientOrigin=(0,0)
  movedFrom=(120,120 702x476)
  parkedAt=(2624,120 702x476)
```

- **默认客户区裁剪**（`IncludeFrame=false`）：输出 680×420，`clientOrigin=(0,0)`；需要标题栏时传 `IncludeFrame=true`。
- PrintWindow 截图内容完整（文本框 `initial`、按钮 `Click Me`、状态 `status: idle`、列表 Alpha..Echo、动画帧号、品红标记）。
- 一次干净运行：前台/光标/剪贴板/lastInput 全部不变。
- 另一次运行：前台/剪贴板不变，光标/lastInput 变化 → 人类在操作，不能算严格通过。
- `stable=false`：FixtureApp 每 100ms 动画，预期；静态窗口会稳定。

## 3. DPI 发现（重要）

第一次用 PowerShell 启动 FixtureApp（DPI-unaware），CLI 为 PerMonitorV2：

- `GetWindowRect`/PrintWindow 返回 1044×689；
- 实际应用只画了左上约 696×451，其余是黑边；
- 说明 DPI-unaware 目标在 PMv2 调用方下会出现"物理尺寸 DC + 逻辑尺寸绘制"的错配。

把 FixtureApp 改为 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` 后，窗口为 702×476@150%，截图默认客户区裁剪为 680×420，内容完整。

**结论**：DPI-unaware 的遗留应用需要 P4 专项处理（按目标 DPI awareness 选择 DC 尺寸/缩放，或在专用 unaware 线程捕获）。v1.0 兼容矩阵必须标注"DPI-aware 已验证；legacy DPI-unaware 待测"。

## 4. 限制

- CLI 是**一次性进程**：`shot` 停泊→截图→还原；没有 daemon，所以不能跨命令持有 target。
- `shot` 会移动窗口，必须 `--confirm`；用户窗口仅短暂离屏。
- `CaptureEngine` 目前不缩放（MaxWidth 预留）、不缓存、不做连续流。
- `CaptureEngine` 的稳定等待最多约 1s（`WaitStableMs*4`），适合截图，不适合视频。
- WGC 只作为"可见窗口"兜底；离屏仍以 PrintWindow 为主。

## 5. 证据

| 文件 | 内容 |
|---|---|
| `cli-shot.png` | 702×476 PrintWindow，完整夹具 UI |
| `cli-shot.json` | 结构化目标/捕获元数据 |
| `cli-state-before.json` / `after.json` | 前后系统状态（其中一次含人类输入污染） |

## 6. 下一步

1. P2：TargetRegistry / TargetSession / daemon，让 CLI/MCP 跨命令持有 target。
2. P1-04：WGC 可见/VDD 后端（仅可见窗口/虚拟显示器）。
3. P1-12：安静环境重跑。
4. P4：DPI-unaware 应用专项；多显示器/混合 DPI。
