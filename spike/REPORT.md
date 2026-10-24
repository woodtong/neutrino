# P0 可行性尖刀报告

> 日期：2026-10-22 / 2026-10-23
> 机器：Windows 11 26200（家庭版）、2560×1600 @150%、Intel + RTX 5060 Laptop、.NET 8.0.418、Edge。
> 代码：`spike/GhostScreen.Spike` + `src/GhostScreen.Windows` / `Capture` / `Input` / `Web`。
> 证据：`spike/evidence/`（截图、日志、summary.json）。

## 0. 结论摘要

**G0 结论：有条件通过。原计划的主路径假设 H1 被证伪，但产品仍有可行路径。**

- ❌ **WGC 对完全离屏窗口不产生新帧**。它会保留最后一次可见时的合成结果；从未可见的窗口客户区是空白。
- ✅ **PrintWindow（PW_RENDERFULLCONTENT）在离屏状态下是活的**：FixtureApp 连续 8 次拉取得到 8 个不同 hash；帧号 39 → 49；p50 11.9ms / p95 13.0ms。
- ✅ **停泊与不可见性成立**：窗口停在 `(2624,120)`（虚拟屏 2560×1600 之外），物理屏上的品红标记像素为 0；PrintWindow 画面里的标记像素为 1600。
- ✅ **前台/剪贴板不变量成立**：FixtureApp 全流程、Notepad 操作、UIA 操作前后前台未变；剪贴板序列号未变。
- ⚠️ **光标不变量未在“无人操作”条件下证明**：本机测试期间 `GetLastInputInfo` 变化，说明有人类输入；代码路径没有调用 `SetCursorPos`，但需要在安静机器上重测。
- ✅ **UIA + 窗口消息输入可用**：UIA Invoke/SetValue；WinForms 的 BM_CLICK/WM_SETTEXT；Notepad 的 RichEditD2DPT WM_SETTEXT（“hello 世界 123”）。
- ✅ **无头 Edge + CDP 可用**：标题/状态/帧号读取、坐标点击、console 捕获、桌面/移动截图（390×844 dpr=3）。
- 🔁 **架构调整**：离屏原生窗口的默认捕获改为 **PrintWindow**；WGC 仅用于“窗口在物理/虚拟显示器上可见”的场景（含 VDD）；网页继续走 CDP。

## 1. 环境与方法

- 进程启动时设置 **PerMonitorV2**；所有几何坐标为物理像素。
- FixtureApp 是 WinForms，带输入框、按钮、状态标签、列表、100ms 动画、40×40 品红标记；`WS_EX_NOACTIVATE`，可无焦点显示。
- 主流程：在屏幕外创建/停泊 → 强制重绘 → WGC 20 帧 → PrintWindow 8 次 → UIA/消息输入 → 关闭。
- 图像验收：除了像素统计，使用 `image-viewer` 子代理逐张查看截图，确认文字/控件/帧号。

## 2. H1：WGC 离屏是否持续出帧

| 场景 | 结果 | 证据 |
|---|---|---|
| 窗口在屏幕外且从未可见 | 20 帧只有 **1 个唯一 hash**；客户区空白，只有标题栏 | `fixture-wgc.png`、`run-offscreen-repaint.log` |
| 先在屏幕上绘制，再停泊 | 20 帧只有 **1 个唯一 hash**；客户区内容保留（品红 1600 px），但动画冻结 | `run-visible.log` |
| 只留 1px 在屏幕内 | 20 帧只有 **1 个唯一 hash**；没有恢复更新 | `run-sliver.log` |
| 窗口保持可见 | 10 帧 **10 个唯一 hash** | `fixture-wgc-visible.png`、`fixture-wgc-visible-9.png` |
| 可见窗口 WGC 前后 | 前台不变；检测到人类输入 | `run-fixture-full.log` |

**结论**：WGC 捕获的是 DWM 的窗口合成表面；窗口完全离开显示器后，DWM 不再更新该表面。1px 露出不足以让它保持活跃。WGC 不能作为“完全离屏窗口”的实时后端。

**保留用途**：虚拟显示器（VDD）上的窗口、临时可见的窗口、Dashboard 实时预览、未来需要连续帧的场景。

## 3. H2：PrintWindow 兜底是否可用

- FixtureApp：702×476，非黑，8 次拉取 **8 个唯一 hash**；连续两帧的动画文字为 `frame 39` → `frame 49`（看图确认）。
- 性能（FixtureApp，702×476）：**p50 11.9ms，p95 13.0ms**（8 次；含截图编码前的原始 BGRA 拉取）。
- Notepad：1920×1125，非黑；完整 Win11 Notepad UI；写入后文档正文为 `hello 世界 123`，状态栏 `12 个字符`。
- **关键机制**：PrintWindow 会触发窗口绘制/取重定向表面，因此在离屏状态下仍能得到当前内容；WGC 不会。
- **尺寸差异**：PrintWindow 包含窗口边框（702×476），WGC 对当前窗口返回 684×467（客户区）。产品必须统一裁剪与坐标元数据。
- **未验证**：Chromium/Electron/UWP/WinUI/游戏/DRM；PrintWindow 对挂起窗口可能阻塞，必须加超时或隔离执行。

证据：`fixture-printwindow-0.png`、`fixture-printwindow-7.png`、`notepad-printwindow.png`、`notepad-typed.png`。

## 4. H3：停泊、不可见性与状态不变量

- 停泊后窗口：`bounds=(2624,120 702x476)`，`visible=true`，`minimized=false`，`cloaked=false`，`tool=true`，完全在 2560×1600 虚拟屏之外。
- 物理屏全屏采样：品红标记像素 **0**；同一窗口的 PrintWindow 画面：品红标记 **1600 px**。
- FixtureApp 全流程：前台 `0x210FE0` 不变；剪贴板 seq 272 不变；`lastInput` 变化 → 人类在动鼠标，光标不变量被污染。
- Notepad 初次测试（未加 `WS_EX_NOACTIVATE`）会抢前台；加入 `WS_EX_NOACTIVATE` 并在动作后 `SetForegroundWindow(原前台)` 后，前台保持不变。
- `ForegroundGuard` 对 UIA 的异步激活有效：UIA 动作后前台恢复为原窗口。

**结论**：显示隔离成立；输入/焦点隔离在“我们启动并加了 NOACTIVATE 的窗口”上成立。任意第三方应用仍可能在启动时自激活，必须用 guard 恢复并验证。

## 5. H5/H6：UIA 与窗口消息输入

- UIA 快照：17 个元素；找到 `Edit/txtInput`（Value/Text）、`Button/Click Me`（Invoke）、`List/Alpha..Echo`。
- UIA Invoke → `status: clicked 1`；UIA SetValue → 输入框 `uia 世界`；前台不变。
- 窗口消息：WinForms 类名是 `WindowsForms10.BUTTON...` / `WindowsForms10.EDIT...`，不能按 `"Button"`/`"Edit"` 精确匹配；按类名包含 `BUTTON`/`EDIT` 匹配后，`BM_CLICK` → `status: clicked 2`，`WM_SETTEXT` → `msg 123`。
- Notepad：`RichEditD2DPT` + `WM_SETTEXT` 成功，中文正常。
- **未验证**：Chromium/Electron 是否忽略这些消息（预期会忽略）；UWP/WinUI 的 ValuePattern 覆盖；UIA provider 的激活行为差异。

## 6. H7：无头 Edge + CDP

- 页面：`spike/testpage/index.html`；标题 `GhostScreen Spike`，按钮 `Click Me`，状态 `idle`，动画 `frame N`。
- 读取：标题/状态/帧号；坐标点击 `#btn`（89.7,157.0）→ 状态 `clicked`；console 捕获 `log:spike-console`。
- 截图：`web-before.png` 1250×705；`web-after.png` 1250×705；移动端 `web-mobile.png` 1170×2532（CSS 390×844，dpr=3）。
- 清理：Edge profile 删除；无 `edge-profile` 参数的 msedge 进程残留。
- **P0 限制**：spike 使用固定端口 9222 与固定 profile 路径；生产启动器必须改为策略化、可配置、免动态命令拼接的实现。

## 7. H8/H9：未测试

- H8（多显示器/混合 DPI）：本机单显示器；只验证了 150% 单屏。P4 前需要外接显示器或 VDD。
- H9（VDD）：本机没有安装虚拟显示器；未测试。P5/v1.x 再做。

## 8. 架构影响

1. **捕获后端选择**：
   - web → CDP；
   - 原生离屏 → **PrintWindow 优先**；
   - 原生可见/VDD → WGC；
   - 两者都失败 → 结构化错误，不返回黑图。
2. **停泊流程**：隐藏/离屏启动 → 停泊 → `WS_EX_NOACTIVATE` → `ShowWindow(SW_SHOWNOACTIVATE)` → PrintWindow。WGC 需要窗口至少可见绘制过，不再作为离屏主路径。
3. **尺寸/坐标**：PrintWindow 含边框，WGC 客户区；统一在 CaptureEngine 里裁剪并返回 `client_origin_in_image`。
4. **焦点**：`ForegroundGuard` 只恢复“我们导致”的前台变化；不覆盖人类主动切换。
5. **性能**：PrintWindow 12ms 级别，适合按需截图；不适合连续视频。连续预览仍用 WGC/VDD。
6. **风险**：PrintWindow 对 Chromium/Electron/UWP 的兼容性未知；对挂起窗口可能阻塞；需要超时/隔离。

## 9. 风险与未决问题

| 风险 | 影响 | 下一步 |
|---|---|---|
| PrintWindow 对 Chromium/Electron/UWP 全黑 | 原生现代应用覆盖不足 | P1 用 Edge GUI/VS Code/Calculator 实测；必要时 VDD 或 CDP |
| PrintWindow 阻塞/卡死 | daemon 卡住 | P1 加超时/隔离 worker；记录 hung 应用 |
| 光标不变量未被安静环境证明 | 无法宣称“绝不移动光标” | P1 在无人操作时重跑；或加输入监控并断言 |
| 1px 露出无效 | 不能靠“露一点”保活 | 放弃该方案；VDD 或 PrintWindow |
| 固定端口/profile 的 spike 启动器 | 不可分发 | P1/P2 改为策略化启动器 + 端口分配 |
| 单显示器 | 多屏 DPI 未知 | P4 专项 |
| WGC 离屏冻结 | 不能做离屏实时流 | 用 PrintWindow 拉取；流式预览用 VDD |

## 10. P0 出口判定

**有条件通过**：

- 产品路径成立（PrintWindow 离屏实时 + 停车 + 输入 + CDP）。
- 原 H1 假设不成立，架构已修正；不再把 WGC 当离屏主路径。
- 进入 P1 前必须补：
  1. PrintWindow 对 3 个现代应用（Edge GUI/VS Code/Calculator）的兼容性；
  2. PrintWindow 超时/挂起保护；
  3. 安静环境下的光标/焦点不变量重测；
  4. 统一裁剪/坐标元数据。

## 11. 证据索引

| 文件 | 内容 |
|---|---|
| `fixture-printwindow-0.png` / `-7.png` | 同一窗口两次 PrintWindow，帧号 39→49，证明实时 |
| `fixture-wgc.png` | 离屏 WGC：客户区空白（只有标题栏） |
| `fixture-wgc-visible.png` / `-9.png` | 可见 WGC：10/10 不同 hash |
| `notepad-printwindow.png` / `notepad-typed.png` | Notepad 完整 UI；WM_SETTEXT 后出现中文 |
| `web-before.png` / `web-after.png` / `web-mobile.png` | headless Edge 点击前后 + 移动视口 |
| `run-fixture-full.log` | 主流程完整输出（UIA/消息/前台/性能） |
| `run-visible.log` / `run-sliver.log` | WGC 可见 vs 1px 露出对比 |
| `run-offscreen-repaint.log` | 离屏 + 强制重绘仍冻结 |
| `summary.json` | 主流程结构化结果 |

## 12. 复现命令

```bash
# 主流程（离屏 + WGC/PrintWindow + UIA + 消息）
dotnet run --project spike/GhostScreen.Spike -- --notepad   # Notepad 实测
dotnet run --project spike/GhostScreen.Spike -- --edge      # 无头 Edge + CDP
dotnet run --project spike/GhostScreen.Spike -- --wgc-visible
dotnet run --project spike/GhostScreen.Spike -- --cleanup   # 清理测试遗留窗口
```

> `spike/out/` 是本地输出（已 gitignore）；`spike/evidence/` 是归档证据。
