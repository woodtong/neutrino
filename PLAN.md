# 中微子（Neutrino）长程执行计划

> 产品名：中微子（Neutrino）；内部代号/代码命名空间仍为 GhostScreen，技术重命名单独进行。
>
> 版本：v1.0 / 2026-10-22
> 这是**跨会话的唯一执行看板**。每次开工先读本文件 + `doc/05-进度与决策日志.md` 最后一条；收工更新本文件 + `doc/05`。
>
> 关联：
> - 完整技术/限制报告：[`doc/16-技术实现与限制报告.md`](doc/16-技术实现与限制报告.md)
> - 范围/质量：[`doc/07-开源项目规划.md`](doc/07-开源项目规划.md)
> - 架构：[`doc/02-架构设计.md`](doc/02-架构设计.md)
> - 接口：[`doc/03-接口设计.md`](doc/03-接口设计.md)
> - 技术验收：[`doc/04-路线图与验收.md`](doc/04-路线图与验收.md)
> - 市场背景：[`doc/06-市场与需求调研.md`](doc/06-市场与需求调研.md)（仅背景，不作为目标）

## 0. 当前状态

| 项 | 值 |
|---|---|
| 当前阶段 | **P4（0.5.0-dev：Human Consent Gate、UWP 回退待验证）** |
| 代码状态 | MCP stdio **21 工具** + 状态端点 + 托盘协商窗；`0.5.0-dev`；测试 32/32（焦点不变量、WPF UIA、严格协商门） |
| 下一动作 | **UWP 协商门实测；多 Agent 排队/允许 5 分钟；24h/4-target/混沌 soak；P4-04 混合 DPI/双屏/锁屏；干净机器验证** |
| 阻塞 | 混合 DPI 需要第二块屏或 VDD；24h soak 需要长时间后台 |
| 最近更新 | 2026-10-24 |

**阶段进度**

| 阶段 | 状态 | 完成度 | 出口（Gate） |
|---|---|---|---|
| P0 基础 + 尖刀 | ✅ 完成（有条件通过） | 100% | G0：H1 结论明确；仓库/CI 绿 |
| P1 捕获内核 | 🟨 进行中 | 75% | G1：截图正确；不变量；无孤儿 |
| P2 观测/输入/MCP | 🟨 进行中 | 75% | G2：MCP 3 场景；v0.1 alpha |
| P3 Web + Recipes | ✅ 完成 | 100% | G3：三视口；无进程泄漏；v0.2 beta |
| P4 UX + 加固 | 🟨 进行中 | 65% | G4：24h soak；8 应用；v1.0 |
| P5 社区 | ⬜ 未开始 | — | 持续 |

> **P0 结论（2026-10-23）**：H1 不成立（WGC 离屏冻结）；PrintWindow 离屏实时（p50≈12ms）；停车/不可见/前台/剪贴板通过；UIA/消息/CDP 通过。详见 `spike/REPORT.md`。架构已改为 **PrintWindow 主捕获 + WGC 仅可见/VDD + CDP 网页**。

> 状态用 ⬜/🟨/✅。每次收工只改本表 + 对应任务复选框，不靠记忆。

## 1. 北极星与 v1.0 完成定义

**北极星**：Windows 11 上一个**开源、本地、MCP-first** 的隐形窗口执行层——Agent 能真实地看和操作原生应用与网页，而人不被打断。

**v1.0 完成 = 同时满足：**

- [ ] 单二进制 `gs.exe`（self-contained，win-x64），用户无需安装 .NET。
- [ ] 支持矩阵内 **6–8 个应用**做扎实（FixtureApp、记事本、Explorer、无头 Edge、VS Code、计算器/设置、7-Zip 等）。
- [ ] 陌生用户按 Quick Start **5 分钟**跑通「MCP 配置 → 打开 → 截图」。
- [ ] MCP stdio 工具可用：`open/screenshot/observe/click/type/close/panic`（+ 少量扩展）。
- [ ] 全程不抢前台、不移动真实光标、不修改剪贴板；有自动化不变量测试。
- [ ] 24h soak：4 个 target，无句柄/内存持续增长，无孤儿窗口/进程。
- [ ] 崩溃/急停后能还原；下次启动能恢复或明确报告。
- [ ] 文档齐全：README + Quick Start + MCP 配置 + recipes + 架构/API + Troubleshooting + 兼容矩阵 + 基准。
- [ ] 发布物：zip、SHA256、SBOM、CHANGELOG；能签名则签名，不能则明确说明。
- [ ] 无 P0/P1；已知问题有 issue；无遥测；默认安全（attach/interactive 关闭）。

**不是 v1.0 的目标**：完整 Dashboard、BYO VDD、提权模式、ARM64、插件 SDK、会话录制、跨平台、云/账号/付费。

## 2. 成功指标（开源）

| 维度 | 3 个月 | 6 个月 | 12 个月 |
|---|---|---|---|
| Star | 300 | 1,000 | 3,000 |
| Release 下载 | 500 | 2,000 | 8,000 |
| 外部贡献者 | 3 | 10 | 20 |
| Recipes | 5 | 8 | 15 |
| 兼容应用 | 5 | 8 | 12 |
| 版本 | v0.1 alpha | v0.5 beta | v1.0 |
| 质量 | CI 绿、无 P0 | 24h soak、基准 | 稳定 API、2 个集成 |

> Star 是虚荣指标，只作参考；真正看下载、issue 质量、PR、被其他项目引用/集成。无遥测，不做"活跃用户"数字。

## 3. 执行原则（长程不跑偏）

1. **一次只推进一个阶段**。当前阶段 Gate 未过，不启动下一阶段的产品代码（可做无依赖的调研/文档）。
2. **垂直切片**。每个工作块结束时，要么可构建、可运行、可演示；要么明确回滚。不留半成品重构。
3. **风险前置**。P0 先用数据验证最可能推翻架构的 H1（离屏 WGC），再投入 P1。
4. **证据优先**。截图、日志、基准、测试输出进 issue/`evidence/`；"感觉可以"不算通过。
5. **文档随代码**。新增/修改行为 → 同步 API/README/兼容矩阵；不做"以后补"。
6. **范围预算**。任何新增 Must 必须从现有 Must 中移除等量工作，或延长时间；不静默加需求。
7. **质量不可砍**。时间不够时先砍 Could/Should（Dashboard、web snapshot、VDD），不砍测试、不变量、文档、发布闸门。
8. **诚实状态**。README/PLAN 反映真实进度；失败/降级写清楚，不粉饰。
9. **无孤儿**。任何会话结束前检查：无停泊窗口、无残留进程、无未关闭端口。
10. **可持续**。每周留 20% 缓冲；每月复盘一次；不承诺 SLA，不接超范围需求。

## 4. 会话协议（每个工作块）

**开工（5 分钟）**
1. 读本文件 §0 + 当前阶段任务 + `doc/05` 最后一条。
2. 跑 `dotnet build` / `dotnet test`（若已有代码），确认基线绿。
3. 在回复里用一句话说明：当前阶段、本次要完成的任务 ID、验收方式。

**工作中**
- 只做当前阶段的任务；发现新问题 → 记 issue/PLAN 的 Parking Lot，不顺手扩 scope。
- 核心逻辑先写测试；Win32 交互至少留可复现脚本/证据。
- 遇到阻塞 > 30 分钟：记录现象、已尝试、下一步；切到同阶段无依赖任务，或停下来问只有用户能决定的事。

**收工（10 分钟）**
1. `dotnet build` / `dotnet test` 必须绿；GUI 集成跑不了就写明原因与手动验证结果。
2. 更新本文件：任务复选框、§0 状态表、下一动作。
3. 追加 `doc/05`：做了什么、证据、结论、下一步、遗留。
4. 检查无孤儿窗口/进程；清理临时 profile/截图。
5. **提交策略**：默认不自动 commit/push；每个 Gate 或你明确授权时提交。若希望按阶段自动提交，需在批准计划时说明。

**Definition of Ready（任务开工前）**
- 目标/验收清楚；依赖已完成；涉及的应用/API 可访问；不会与另一个进行中的改动冲突。

**Definition of Done（任务收工）**
- 代码 + 测试 + 文档 + 证据；无新增 P1；不破坏既有不变量；PLAN/05 已更新。

## 5. 时间线与发布节奏

> 以"全职 5 天/周、每天 6 小时专注"估算；业余按比例拉长。日历从实际开工日算，不绑定具体日期。

| 阶段 | 周次 | 净人日 | 发布 | 关键产出 |
|---|---|---|---|---|
| P0 基础 + 尖刀 | W1 | 6.5 | — | 仓库/CI/License；H1–H7 报告；go/no-go |
| P1 捕获内核 | W2–W3 | 11.0 | — | 停泊/PrintWindow/WGC/CLI；不变量；基准 |
| P2 观测/输入/MCP | W3–W4 | 7.5 | **v0.1.0-alpha**（W4） | UIA/输入/MCP/doctor；3 场景 |
| P3 Web + Recipes | W5–W6 | 7.0 | **v0.2.0-beta**（W6） | 无头 Edge/CDP；多视口；recipes |
| P4 UX + 加固 | W7–W9 | 10.0 | **v1.0.0-rc**（W9） | 托盘/状态页/恢复/多屏/soak/文档/打包 |
| P4 收尾 + 反馈 | W10–W11 | 缓冲 | **v1.0.0**（W11–12） | 修 rc 问题；干净机器验证；正式发布 |
| P5 社区 | 持续 | — | 月度小版本 | 兼容矩阵/recipes/集成/贡献者 |

- **净工作量**：约 **42 人日**；加 20% 缓冲 ≈ **50 人日**；全职 **10–12 周**（含发布与反馈）。
- **业余**：每周 2–3 天 → **4–6 个月**。
- **每月一个 minor**；紧急修复随时；v1.0 前不承诺日期，只承诺 Gate 标准。

## 6. 工作分解（WBS）

> 估算单位：人日（d）。每个任务都必须有可验证的验收，不接受"写完了"。

### P0 — 基础 + 可行性尖刀（6.5d）✅

- [x] **P0-01 仓库基线**（0.5d）：`git init`、Apache-2.0、`.gitignore`、`.editorconfig`、README 骨架、目录结构。
- [x] **P0-02 解决方案骨架**（0.5d）：Core/Windows/Capture/Input/Web/Daemon/Mcp/Cli/Tests/FixtureApp；`Directory.Build.props`（nullable、warnings-as-errors）。
- [x] **P0-03 CI 基线**（0.5d）：GitHub Actions：build、test、CodeQL、dependency review；format check 待 P1。
- [x] **P0-04 开源文件**（0.5d）：CONTRIBUTING、CODE_OF_CONDUCT、SECURITY、issue/PR 模板、DCO 说明。
- [x] **P0-05 Spike 骨架**（1.0d）：窗口枚举、停泊、还原、状态快照（前台/光标/剪贴板/lastInput）、日志。
- [x] **P0-06 WGC 取帧**（1.0d）：D3D11 设备、帧池、帧计数/时间戳/hash、超时/取消。
  - **结论**：离屏 1/20 唯一 hash（冻结）；可见 10/10（实时）；1px 露出无效。
- [x] **P0-07 PrintWindow 兜底**（0.5d）：`PW_RENDERFULLCONTENT`、空白检测、实时性验证。
  - **结论**：FixtureApp 8/8 帧变化，p50≈12ms；Notepad 1920×1125 完整；Edge GUI/VS Code 待 P1。
- [x] **P0-08 不可见性验证**（0.5d）：GDI 全屏采样 + 品红标记；物理屏标记像素 0。
  - DXGI 逐帧检测待 P4（P0 用全屏采样 + 逻辑离屏已足够）。
- [x] **P0-09 CDP Spike**（0.5d）：无头 Edge 启动、端口解析、截图、DOM、点击、console。
  - **结论**：单次通过；移动端 390×844 dpr=3；100 次循环待 P3。
- [x] **P0-10 UIA/消息 Spike**（0.5d）：UIA Invoke/SetValue；BM_CLICK/WM_SETTEXT；前台 guard。
  - **结论**：FixtureApp/Notepad 通过；Chromium 消息预期忽略，待 P1 记录。
- [x] **P0-11 可行性报告 + 决策**（0.5d）：`spike/REPORT.md`；更新 ADR/01/02/07；go/no-go。

**Gate G0（P0 出口）**✅
- [x] `dotnet build` / 测试 runner 绿。
- [x] 报告有数据与看图验收，不是主观判断。
- [x] H1 结论明确：**离屏冻结**；Plan B 已执行（PrintWindow 主路径）。
- [x] 07/02/01 已更新到 PrintWindow 主路径。

### P1 — 捕获内核（11.0d）

> **P1-11 已完成（2026-10-24）**：Edge GUI PrintWindow 完整；VS Code PrintWindow 只有 chrome、CDP 完整；Calculator PrintWindow/UIA 完整但 UWP 焦点隔离不稳定。详见 `spike/P1-11-REPORT.md`。

- [x] **P1-01 窗口注册表（基础）**：`gs list` + 过滤 tool/空标题；PID/类名/DPI/cloak 已暴露。
  - TargetRegistry（跨命令、生命周期）延后到 P2。
- [x] **P1-02 停泊基础**：Park/MoveTo/Restore、隐藏/最小化启动、NOACTIVATE、前台恢复；`gs shot` 已验证。
  - watch/自动重停泊/启动 flash 优化仍待 P4。
- [x] **P1-03 PrintWindow 主后端**（1.0d）：强制绘制、客户区裁剪、空白/超时检测、隔离 worker。
  - [x] 6s 超时 wrapper（`TryCaptureWithTimeout`）；超时返回结构化错误 `CAPTURE_TIMEOUT`。
  - [x] 挂起应用降级：`IsHungAppWindow` 在 Describe 之前检查并立即 `TARGET_HUNG`；连续 2 次 `CAPTURE_TIMEOUT` 也降级。后续 capture/observe/input 快速失败，close 仍可还原。
  - [x] **attach 前拒绝挂起窗口**（`WindowHealth.IsHung`），避免 `SetWindowPos` 卡死。
  - [x] **per-target worker 复用：决定不做**。PrintWindow 本来就在专用线程，不会拖垮线程池；降级把每个 target 的阻塞线程封顶在 2 个；attach 已拒绝挂起窗口。再加长驻 worker 只会增加队列/取消/COM 复杂度，收益不足。若后续 profiling 显示线程创建或 park 阻塞成为瓶颈，再引入。
  - 验收：FixtureApp/Notepad/Explorer 连续截图；不返回黑图；挂起应用超时可控。
- [ ] **P1-04 WGC 后端（可见/VDD）**（1.5d）：设备/帧池/尺寸变化/超时/取消；BGRA→PNG。
  - 已完成：可见窗口 `WgcCapture.TryCapture` 单帧验证（21/21 测试含 `WGC captures a visible window`）；P0 曾做 10/10 连续帧。
  - 待补：VDD（BYO）路径、尺寸变化/长时间连续帧、取消。
  - 验收：可见窗口连续帧；VDD 可用时全速；不再用于离屏实时。
- [x] **P1-05 CaptureEngine（基础）**：PrintWindow 主路径、可见 WGC 兜底、稳定性、窗口/客户区/DPI 元数据。
  - 缩放、缓存、连续流、DPI-unaware 专项仍待。
- [x] **P1-06 CLI（基础）**：`version/state/list/shot`；`open/close` 与 daemon 跨命令延后 P2。
  - 验收：FixtureApp 702×476 完整截图；前台/剪贴板不变（一次干净运行光标/lastInput 也通过）。
- [ ] **P1-07 FixtureApp**（1.0d）：按钮、输入框、列表、画布、动画、弹窗。
  - 验收：可确定性截图；元素名/位置稳定。
- [ ] **P1-08 不变量测试**（0.5d）：前台/光标/剪贴板/窗口不在显示器内。
  - 验收：每次集成测试自动跑；失败即红灯。
- [ ] **P1-09 基准**（0.5d）：截图 p50/p95、点击端到端、内存/句柄。
  - 验收：数据入 `benchmarks/`；基线记录。
- [ ] **P1-10 Quick Start + demo**（0.5d）：README 草稿、90 秒 GIF/终端 transcript。
  - 验收：陌生人能按步骤跑通。
- [x] **P1-11 现代应用兼容性**（1.0d）：Edge GUI / VS Code / Calculator 的 PrintWindow/CDP。
  - **结论**：Edge GUI PrintWindow ✅；VS Code PrintWindow ❌ / CDP ✅；Calculator PrintWindow ✅ / 焦点不稳定。
- [x] **P1-12 安静环境不变量重测**（0.5d）：用 `GetLastInputInfo` 区分人类输入。
  - [x] `--quiet` 已实现：等待 3s 无输入 → 夹具/UIA/PrintWindow → 严格比对。
  - [x] 2026-10-24 复测：`humanInput=false`，前台/光标/剪贴板全部不变，3 帧 PrintWindow 哈希各不相同，`pass=true`。
  - 验收：无人类输入时，前台/光标/剪贴板三项严格不变。

**Gate G1**
- [ ] 记事本/Edge 截图正确（视觉验收）。
- [ ] 至少 2 个现代应用（Edge GUI/VS Code/Calculator）可用，或明确记录不支持。
- [ ] 最小化/隐藏返回 `NOT_RENDERABLE`，不返回黑图。
- [ ] 停泊全程前台/光标/剪贴板不变（安静环境断言）。
- [ ] 100 次截图无句柄/内存增长；无孤儿。
- [ ] 截图 p50 ≤ 300ms（M1 目标；P0 PrintWindow 已 ≈12ms）。

### P2 — 观测、输入与 MCP（7.5d）

> **P2 基础切片（2026-10-24）**：TargetManager/TargetSession + MCP stdio（9 工具）完成；进程内 8/8、真实 stdio initialize/tools/list 通过；PNG image content、客户区裁剪、EOF 还原。详见 `spike/P2-MCP-REPORT.md`。

- [x] **P2-01 UIA 观测（基础）**：扁平化 Snapshot + ref + epoch；`ClickRef` 过期返回 `ELEMENT_STALE`。
  - 缓存请求/树过滤仍待。
- [x] **P2-02 UIA 动作（基础）**：Invoke + SetValue 接 MCP；ref/name 两条路径。
  - **0.4.1-dev 修订**：原生元素动作先解析 HWND 子控件走窗口消息；无 HWND 时自动走 UIA（Invoke/Toggle/SelectionItem/ExpandCollapse）并恢复前台。原因见 P2-11。
  - Toggle/Selection/ExpandCollapse/Scroll 待补。
- [x] **P2-03 窗口消息输入**（1.0d）：`click`/`scroll`/`drag`/`key`；子窗口命中；Chromium/Electron 拒绝。
  - 键盘为消息级，复杂 IME/快捷键需应用级验证。
  - 验收：经典 Win32/WPF 成功；Chromium 返回 `INPUT_UNSUPPORTED` 而非静默失败。
- [x] **P2-04 InputRouter（基础）**（0.5d）：消息输入能力判断、Chromium/Electron 拒绝、target 上报 input capabilities。
  - [ ] SendInput interactive 模式/策略链。
  - 验收：每个 target 上报可用输入；默认不启用 SendInput。
- [ ] **P2-05 多 target**（0.5d）：注册表、串行化、TTL、配额、清理。
  - [x] TargetManager 多 target + 串行化 + CloseAll；[ ] TTL/配额/崩溃恢复。
  - 验收：4 个 target 并行；关闭/崩溃无残留。
- [x] **P2-06 MCP 服务器（基础）**（1.0d）：stdio、13 个工具、image content、结构化错误。
  - [ ] cancellation、MCP Inspector/真实客户端联调。
  - 验收：MCP Inspector 可见；真实客户端完成 3 场景。
- [ ] **P2-07 `gs doctor`**（0.5d）：OS/Edge/token/WGC/停泊/端口/日志检查；`--bundle` 脱敏。
  - 验收：常见故障能给出下一步命令/文档链接。
- [ ] **P2-08 集成测试**（1.0d）：FixtureApp/Notepad/Explorer；不变量；错误路径。
  - [x] MCP in-process 8/8（attach/shot/observe/click/set_text/close）；[ ] Notepad/Explorer/失败路径。
  - 验收：主路径 + 至少一个失败路径；全套绿。
- [x] **P2-09 文档（基础）**（0.5d）：`doc/08-MCP客户端配置.md`（Claude Code/Codex/Cursor/ZCode/通用）。
  - [x] 真实客户端联调：官方 `@modelcontextprotocol/sdk` 1.32.2（Node 22）通过 stdio 完成 initialize、20 工具、open_web、PNG image content、observe、close_all；客户端日志见 doc/05。
  - [ ] 干净机器解压验证（无 .NET / 新用户）仍待做。近似验证：复制到 `%TEMP%`、PATH 去 dotnet，自包含 CLI/MCP 20 工具通过。
  - 验收：照文档可接 2 个客户端。
- [x] **P2-10 v0.1.0-alpha 发布（本地包）**（0.5d）：self-contained MCP/CLI、zip、SHA256、发布说明；发布版 stdio 冒烟通过。
  - [ ] 干净机器/真实客户端验证。
  - 验收：干净机器可解压运行；release notes 诚实。
- [x] **P2-11 焦点安全（0.4.1-dev）**（0.5d）：执行优先，拒绝兜底。
  - 实测：WinForms/WPF 的 UIA `Invoke`/`SetValue`/`Toggle`/`Select` 都会激活 `WS_EX_NOACTIVATE` 停泊窗口；窗口消息 `BM_CLICK`/`WM_SETTEXT`/`WM_KEYDOWN` 不改变前台，但无法触发 WPF 控件。
  - 实现：`click_element`/`set_text` 按 UIA bounds 找 HWND 子控件走消息；无 HWND 时自动走 UIA pattern 并恢复前台；坐标 `click` 在 WPF/UWP 单 HWND 上用 UIA `AtPoint`；`strict_no_focus_steal=true` 才拒绝；没有可执行 pattern 返回 `ELEMENT_NOT_ACTIONABLE`；`FocusGuard` 500ms 观察 + 100ms 稳定校验，第三窗口不抢回。
  - 测试夹具默认屏外创建；WGC 可见测试需 `GHOSTSCREEN_VISIBLE_TESTS=1`；WPF UIA 测试需 `GHOSTSCREEN_UIA_TESTS=1`；测试结束断言无可见窗口泄漏。
  - 验收：MCP 消息路径前台/光标/剪贴板不变；WPF Invoke/Toggle/SelectionItem/ValuePattern 均执行并恢复前台；29/29 绿。

- [x] **P2-12 Human Consent Gate（0.5.0-dev）**（1.0d）：默认 strict，UIA 动作先问人。
  - `ConsentManager`：按 target/action/element 计数，3 次后创建 consent；15s 自动放行、30s 票有效期、一次性消费、绑定具体动作。
  - MCP：`USER_ACTIVE`（retryable + attempts/max_attempts/retry_after_ms）、`AWAITING_USER`（consent_id/expires_at）、`USER_DENIED`、`CONSENT_EXPIRED`；`click_element`/`set_text`/`click` 接受 `consent_id`。
  - 托盘：非激活小窗，允许/拒绝 + 15s 倒计时；状态端点暴露 pending consent，`POST /api/consent` 用 loopback token 决策。
  - 消息/CDP 路径永不被门禁阻塞；`balanced`/`force` 可切换。
  - 验收：32/32；WPF 严格门测试验证前两次 USER_ACTIVE、第三次 AWAITING_USER、允许后执行、拒绝后不执行。

**Gate G2**
- [ ] MCP 客户端完成「打开记事本→输入中文→截图确认」与「打开网页→截图→点击」。
- [ ] 输入不变量通过；无焦点/光标漂移。— 0.5.0-dev 消息路径、WPF UIA 回退与严格协商门已通过；UWP 待实测。
- [ ] 错误有 code/hint；取消不残留。
- [ ] v0.1 alpha 发布。

### P3 — Web 运行时与 Recipes（7.0d）

- [x] **P3-01 Edge 生命周期**（1.0d）：发现、无头启动、端口解析、profile 隔离、进程树清理。
  - 已实现：固定安装路径发现；`--headless=new`；`--remote-debugging-port=0` + `DevToolsActivePort`；每 target 独立临时 profile；关闭时杀进程树并删 profile。
  - 待补：100 次开关压力测试（P3-09）。
- [x] **P3-02 最小 CDP 客户端**（1.0d）：WebSocket、JSON-RPC、Target/Page/Runtime 域。
  - 已实现：手写 ClientWebSocket 客户端；请求/事件分流；console 事件缓存（上限 500）；超时与断线处理；`attach_cdp` 连接已有 Chromium/Electron 端点（0.3.1），close 只 detach。
- [x] **P3-03 截图与等待**（1.0d）：视口/整页/裁剪；fonts.ready + network idle。
  - 已完成：视口、整页（16384 上限）、任意 clip；fonts.ready；network idle（300ms 静默，3s 上限）；截图后恢复视口与页面缩放。
  - 像素 diff（headed vs headless）推迟到 P4 保真度任务；需要可见 headed 实例，可能打扰桌面。
- [x] **P3-04 DOM 观测**（0.5d）：可交互元素、selector、bbox、可见性过滤。
  - 已实现：button/a/input/textarea/select/[role=button]/[contenteditable]/[onclick]；稳定 CSS path；零尺寸过滤；ref/epoch 过期检测。
- [x] **P3-05 CDP 输入**（1.0d）：click/type/key/scroll/drag；中文输入。
  - 已实现：click/scroll（合成手势）/drag/key（含 Ctrl/Shift/Alt/Meta）/set_text（React 兼容 native setter + input/change）；中文“mcp 世界”与 Ctrl+S 已实测。
- [x] **P3-06 console/network**（0.5d）：console 级别、失败请求、4xx/5xx、JS 异常。
  - 已实现：console 级别/文本（500 条）；network 失败与 4xx/5xx（200 条）；本地页面的连接失败已实测捕获。
- [x] **P3-07 `gs web snapshot`**（0.5d）：多视口 + 横向溢出检测。
  - 已实现：`snapshot` 一次多视口截图，返回 `horizontal_overflow/scroll_width/offenders`（selector/bounds），结束后恢复视口；400/900 双视口实测。
- [x] **P3-08 Recipes**（0.5d）：Edge/Chrome/VS Code 启动参数与限制。
  - 已完成：[doc/12-Recipes.md](doc/12-Recipes.md)：无头 Edge、Edge GUI、VS Code CDP、UWP、本地前端循环、响应式、原生应用、兼容矩阵、安全边界。
- [x] **P3-09 测试**（0.5d）：本地静态站点（动画/表单/慢请求/错误）；泄漏检查。
  - 已完成：本地静态站点全链路（截图/整页/裁剪/observe/点击/输入/按键/滚动/拖拽/console/network/snapshot/视口/导航/关闭）；`tests/GhostScreen.WebStress` 100 次开关 0 失败，句柄/内存封顶，profile 0。
  - 待补：慢请求用例（network idle 已实现，但未用慢服务验证）。
- [x] **P3-10 v0.2.0-beta 发布**（0.5d）：release notes + 已知限制。
  - 已发布 **0.2.2-beta**（G3 完成）；发布说明 `doc/13-0.2.2-beta发布说明.md`，配方 `doc/12-Recipes.md`。

**Gate G3**
- [x] 三视口截图正确；横向溢出能检出。— `snapshot` 390/768/1440 + 400/900 实测，`#wide` 定位。
- [x] console/network 可用。— `console` 捕获 `boom`；`network` 捕获连接失败与 4xx/5xx。
- [x] 关闭后无 Edge 进程/profile 残留。— 100 次开关 0 失败，profiles 0。
- [x] v0.2 beta 发布。— `0.2.2-beta`（2026-10-24）。

### P4 — UX、加固与 v1.0（10.0d）

- [ ] **P4-01 托盘**（1.0d）：NotifyIcon、状态、target 列表、暂停/恢复、急停、退出还原。
  - 已完成（首版）：独立 `GhostScreen.Tray.exe`（WinForms NotifyIcon，单实例）；每 2s 读 `status.json` 并轮询 `/api/status`；tooltip 显示版本/target 数；菜单：Targets 子菜单（每个 target 标题/状态/degraded_reason，点击确认后 `/api/close_target`）、打开状态页、急停、退出；未运行/不可达/有 target 三态区分。
  - 待补：暂停-恢复、登录自启、降级态图标区分。
  - 验收：无 target/有 target/降级/错误四态区分。
- [ ] **P4-02 极简状态页**（1.0d）：静态 HTML/JS、target 卡片、最近截图（2–5fps）、能力、急停。
  - 已完成（首版）：MCP 进程内 loopback 状态端点（原始 TCP，无 Http.sys/管理员）；token 强制；`/` 页面、`/api/status` JSON（targets/能力/恢复摘要）、`/api/screenshot`（单 target PNG，2s 超时）、`/api/panic`；端点信息写入 `status.json`，退出删除；1s 状态轮询 + 5s 缩略图刷新（0.2fps，避免 PrintWindow 强制重绘开销）；无头 Edge 渲染验证（卡片/缩略图/版本/急停可见）。
  - 待补：2–5fps 连续截图（需要限流/增量/编码成本评估，当前 0.2fps 足够观察）、多客户端/独立 daemon 生命周期、页面视觉走查。
  - 验收：延迟 <1s；关闭页面不影响 daemon。
- [x] **P4-03 崩溃恢复**（1.0d）：`state.json`、启动还原、watchdog/孤儿清理。
  - 已实现：每个 target 写入 `%LOCALAPPDATA%\GhostScreen\state\targets.json`（临时文件原子替换）；启动时按 hwnd+pid+窗口类校验并 `SetWindowPlacement` 还原；web target 记录 pid/启动时间/profile，启动时杀孤儿浏览器并删 profile；清理 24h 以上的陈旧 profile；损坏的 journal 移到 `.corrupt-*` 并继续启动。
  - 验收：杀 MCP 后重启的等价模拟（第二个 manager 用同一 journal）能还原窗口；孤儿 Edge 被杀、profile 删除；`STATE_WRITE_FAILED` 在写入失败时明确报错。
- [ ] **P4-04 多屏/DPI/会话变化**（1.5d）：多显示器、混合 DPI、虚拟桌面、锁屏/唤醒。
  - 已完成：`WindowInfo.DpiAwareness`（Unaware/System/PerMonitor）与 `DPI_UNAWARE` warning；负坐标虚拟桌面布局/停车点单测；**DPI-unaware 捕获修复**（unaware 线程上下文 + StretchBlt 到物理尺寸，150% 单屏验证，自动测试）。
  - 未完成：第二块物理屏/混合 DPI 的点击命中、锁屏/唤醒；当前机器单屏，记为硬件阻塞。
  - 验收：150%+100% 双屏点击命中；锁屏后恢复或明确降级。
- [ ] **P4-05 24h soak + 混沌**（1.0d）：4 target；杀进程、最小化、切桌面、断网。
  - 已完成：`tests/GhostScreen.Soak`（可配置时长/间隔/native/web 数量，预热后基线）；单目标 3 分钟 29 轮 0 失败（句柄 +6、私有 +2MB）；4 目标 3 分钟 24 轮/48 web target 0 失败（句柄 +9、私有 -4MB、profile 0）；4 目标 5 分钟 39 轮/78 web target 0 失败（预热后句柄 +1、私有 -3MB、profile 0）。
  - 待补：24h 跑满、杀进程/最小化/切桌面/断网混沌。
  - 验收：无增长、无孤儿、无静默失败。
- [ ] **P4-06 性能**（0.5d）：截图/点击优化；基准记录。
  - 验收：p50 ≤ 150ms（1280×800，WGC）。
- [ ] **P4-07 文档完整**（1.0d）：架构/API/兼容矩阵/Troubleshooting/FAQ/对比/基准。
  - 验收：每个错误码有条目；Quick Start 5 分钟。
- [ ] **P4-08 打包/分发**（1.0d）：zip、winget/Scoop、SHA256、SBOM、签名（SignPath OSS 或说明未签名）。
  - 验收：干净机器解压即用；校验和正确。
- [ ] **P4-09 干净机器验证**（0.5d）：新用户配置/无 .NET/无 VS 环境。
  - 验收：按 README 一次通过。
- [ ] **P4-10 rc 反馈修复**（1.0d）：v1.0.0-rc 给 3–5 个设计伙伴；修 P0/P1。
  - 验收：无阻塞发布的已知问题。
- [ ] **P4-11 v1.0.0 发布**（0.5d）：release notes、演示、公告。
  - 验收：v1.0 完成定义全部满足。

**Gate G4（发布闸门）**
- [ ] 07 §2.2 全部通过。
- [ ] 24h soak、8 应用矩阵、5 分钟 Quick Start。
- [ ] 无 P0/P1；已知问题有 issue。
- [ ] 文档/演示/校验和/SBOM 齐全。
- [ ] 干净机器验证通过。

### P5 — 社区与持续（不设上限）

- [ ] 每月至少 3 个 good-first-issues。
- [ ] 每月一个 minor release（有内容才发）。
- [ ] 兼容矩阵每两周更新。
- [ ] 每季度依赖升级 + 安全复查。
- [ ] 贡献者 ≥3 后引入第二位 maintainer。
- [ ] v1.x：BYO VDD、完整 Dashboard、i18n 完整化、ARM64（按需求排序）。

## 7. 质量闸门（G0–G4）

| Gate | 时机 | 必须满足 | 不满足时 |
|---|---|---|---|
| **G0** | P0 末 | H1 有数据结论；仓库/CI 绿；Plan B 明确 | 停止 P1，2 天内重定范围；不"再试试"拖两周 |
| **G1** | P1 末 | 截图正确；不变量；无孤儿；p50 ≤300ms | 不加新功能；先修捕获/停泊 |
| **G2** | P2 末 | MCP 3 场景；错误可读；v0.1 alpha | 不进入 Web；先修输入/可观测性 |
| **G3** | P3 末 | 三视口；无进程泄漏；v0.2 beta | 不进入 UX；先修 Web 稳定性 |
| **G4** | P4 末 | 07 §2.2 + 24h soak + 干净机器 | 不发 v1.0；发 rc 继续修 |

## 8. 风险、触发器与应急

| 风险 | 触发器 | 应对 |
|---|---|---|
| **H1 已发生：WGC 离屏冻结** | 已确认（1/20 唯一 hash） | 已切换 PrintWindow 主路径；WGC 仅可见/VDD；1px 露出方案废弃 |
| PrintWindow 对 Electron 失败 | VS Code 只有 chrome（已确认） | 用 CDP 启动/截图；PrintWindow 不作为 Electron 主路径 |
| UWP 启动抢焦点 | Calculator 前台被抢，恢复 API 失败 | 默认不承诺 UWP 无焦点；需显式授权/独立会话；记录矩阵 |
| PrintWindow 卡住（目标无响应） | daemon 卡顿 | P1-03 超时 + 隔离 worker；记录不支持应用 |
| 微软 Agent Workspace 开放 API | 官方 GA + 第三方 MCP/API | 不重写；评估把它作为 backend；强调本地/中立/兼容性 |
| 兼容性长尾 | 支持 >1 人日/周/10 用户 | 收窄矩阵；拒绝不支持；把 recipe 交给社区 |
| 输入隔离达不到 | 某应用必须 SendInput | 明确标"interactive required"；默认禁用；不偷偷用 |
| GUI 测试无法在托管 CI | PR 无法自动验证 UI | 单测覆盖核心；self-hosted runner + 本地脚本；release 附证据 |
| 时间超支 >20% | 周复盘发现 | 按 §9 削减顺序砍功能，不砍质量 |
| 维护者倦怠 | 连续 2 周无进展 | 暂停发布；只 triage；缩小 v1.0 范围 |
| 安全事件 | 误操作/注入/泄露报告 | 立即发公告/补丁；复盘；必要时禁用 attach/interactive |
| 名称/商标冲突 | 发布前检索 | 改名（备选 GhostFrame）；保留迁移说明 |
| 签名失败 | SignPath 未批 | 明确"未签名" + SHA256 + 安装说明；不隐瞒 |

**H1 失败的 Plan B（详细）**
1. **部分可见 + 遮挡**：只露出 1px 或在屏幕边缘被遮挡；验证是否仍有帧。缺点：可能有视觉痕迹，需用户接受。
2. **BYO VDD**：检测用户已装虚拟显示器，把窗口停泊到虚拟屏；需要 P4 提前，且依赖第三方驱动。
3. **缩小范围**：v1.0 只支持"我们启动的 Chromium/Electron + CDP"和"经典 Win32 + PrintWindow"；放弃通用 WGC 离屏。
4. **暂停**：如果 1–3 都不能在 1 周内给出稳定 demo，停止项目或转为纯研究/文档。

## 9. 范围与变更控制

**Scope 预算**
- Must 固定（07 §3）；新增 Must 必须移除等量 Must 或延长时间。
- Should 可在时间不足时砍；Could 默认不进 v1.0。
- 变更记录在 `doc/05` + ADR；口头同意不算。

**砍功能顺序（时间不足时）**
1. 完整 Dashboard（本来后置）
2. `gs web snapshot` 便捷层
3. 状态页实时预览（保留列表/急停）
4. BYO VDD / 提权 / i18n
5. 部分 Should 应用适配（保留矩阵内核心 5 个）
6. **绝不砍**：不变量测试、不可见性验证、恢复/急停、文档、发布闸门

## 10. 节奏与复盘

- **每个工作块**：按 §4 会话协议。
- **每周（30 分钟）**：更新 §0；对比计划 vs 实际；看开放 P0/P1；跑一次集成；记录 1 个风险。
- **每两周**：一个可演示增量（GIF/终端 transcript）。
- **每月**：重估 WBS/时间线；检查范围蔓延；更新兼容矩阵；依赖升级；贡献者情况。
- **每季度**：架构/安全/性能复查；决定 v1.x 方向。
- **发布**：v0.1 alpha（W4）、v0.2 beta（W6）、v1.0 rc（W9）、v1.0（W11–12）；之后月度 minor（有内容才发）。

## 11. 跟踪与证据

**唯一看板**：本文件（复选框 + §0）。
**日志**：`doc/05-进度与决策日志.md`（每次工作块追加）。
**证据**：
- `spike/REPORT.md`（P0）
- `benchmarks/`（基准 JSON/表）
- `docs/compatibility.md`（矩阵 + 证据链接）
- release notes / CHANGELOG
- issue 里的复现步骤、截图、日志、trace id

**GitHub 标签建议**
`area:capture` `area:input` `area:web` `area:mcp` `area:ux` `quality` `docs` `compat` `good-first-issue` `P0` `P1` `P2` `P3`

**每周状态模板**
```md
## 周状态 YYYY-WW
- 阶段：
- 完成：
- 未完成/原因：
- 风险：
- 下周 3 件事：
- 需要用户决定：
```

## 12. 前 10 个工作块（直接开工）

> P0 已完成（2026-10-23）；下表是 P0 的回顾。下一步从 **P1-01 + P1-11** 开始。

| # | 目标 | 出口 |
|---|---|---|
| 1 | 仓库基线 + 解决方案骨架（P0-01/02） | `dotnet build` 通过；目录/README/License 就位 |
| 2 | CI + 开源文件（P0-03/04） | Actions 绿；模板可用 |
| 3 | Spike：窗口枚举 + 停泊 + 状态快照（P0-05） | 记事本停泊/还原；三项状态一致 |
| 4 | WGC 取帧 + 帧 hash（P0-06） | 离屏窗口有持续帧；数据记录 |
| 5 | PrintWindow + 空白检测（P0-07） | 三应用成功/空白统计 |
| 6 | 不可见性 DXGI 检测（P0-08） | 逐帧证据：窗口未出现 |
| 7 | CDP spike（P0-09） | 截图/DOM/点击/console 成功；无进程残留 |
| 8 | UIA/消息 spike（P0-10） | 无焦点操作成功；Chromium 限制记录 |
| 9 | P0 报告 + G0 决策（P0-11） | H1 结论 + Plan B + 范围确认 |
| 10 | P1 启动：窗口注册表 + 停泊管理器（P1-01/02） | `gs list` 可用；往返稳定 |

## 13. 资源与假设

- **人力**：1 名维护者（你 + 我），全职或业余；不假设有额外开发者。
- **硬件**：Windows 11 测试机（当前 26200）、Edge；单显示器。H8（多屏 DPI）需要外接显示器或 BYO VDD，P4 前解决。
- **GitHub**：P0 建仓；v0.1 前可私有，v0.1 公开。Issue/Milestone 与 PLAN 同步。
- **签名**：P4 前申请 SignPath Foundation；未批则明确未签名。
- **名称**：发布前检索/商标；备选 `GhostFrame`。
- **提交**：默认不自动 commit/push；Gate 或你授权时提交。若希望每阶段自动提交，请明确授权。
- **时间**：估算按 6 小时/人日；中断/会议不计。业余按比例拉长，不压缩 Gate。

## 14. 立即执行的第一步

1. **P0-01**：`git init` + Apache-2.0 + `.gitignore` + `.editorconfig` + README 骨架 + 目录。
2. **P0-02**：解决方案骨架 + `Directory.Build.props`；`dotnet build` 通过。
3. **P0-03/04**：CI + 开源文件。
4. **P0-05**：Spike 骨架；跑通"记事本停泊→还原"。

完成后在 `doc/05` 追加第一条执行记录，并把 §0 的 P0 改为 🟨。

## 15. 变更记录

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-10-22 | v1.0 | 初始长程计划：定位开源、P0–P5、WBS、Gate、风险、会话协议 |
| 2026-10-23 | v1.1 | P0 完成：WGC 离屏冻结；PrintWindow 主路径；P1 增至 11d 并新增现代应用/安静环境任务 |
| 2026-10-24 | v1.2 | P1-11 完成：Edge GUI PrintWindow ✅；VS Code 需 CDP；Calculator 焦点不稳定 |
| 2026-10-24 | v1.3 | PrintWindow 6s 超时（专用后台线程）；P1-12 静默测试实现，干净复测待完成 |
| 2026-10-24 | v1.4 | CaptureEngine + CLI（list/state/shot）完成基础切片；发现 DPI-unaware 目标黑边，P4 专项 |
| 2026-10-24 | v1.5 | TargetManager + MCP stdio（9 工具）基础切片；客户区裁剪默认；MCP 测试 8/8 |
| 2026-10-24 | v1.6 | MCP 增加 click（客户区坐标）+ observe epoch/ref；10 工具；坐标点击测试通过 |
| 2026-10-24 | v1.7 | MCP 增加 scroll/drag；InputRouter 拒绝 Chromium 消息；测试 9/9 |
| 2026-10-24 | v1.8 | MCP 增加 key；13 工具；MCP 客户端配置文档（doc/08） |
| 2026-10-24 | v1.9 | 0.1.0-p0 alpha 包：self-contained MCP/CLI、zip/SHA256、发布说明；发布版 stdio 冒烟 |
| 2026-10-24 | v1.10 | P3 web target 接入 MCP：无头 Edge/CDP、18 工具、web 全链路测试（13/13）；版本统一到 ProductInfo 0.2.0-p0 |
| 2026-10-24 | v1.11 | 0.2.0-p0 alpha 包（103.2 MB，SHA256 CAB4B40A…6C0817）；发布版 18 工具 + open_web 冒烟通过；发布说明 doc/10 |
| 2026-10-24 | v1.12 | Edge 启动白名单（仅固定安装根 msedge）+ 测试 14/14；安全复查确认启动参数无 shell/无用户 URL |
| 2026-10-24 | v1.13 | P3-03/07/09：整页/裁剪、network idle、多视口 snapshot+溢出、network 失败、100 次泄漏；0.2.1-p0 alpha（SHA256 B3CD9001…C56F08） |
| 2026-10-24 | v1.14 | P3 完成：recipes（doc/12）、G3 全绿；发布 0.2.2-beta（SHA256 1192775A…B39652F） |
| 2026-10-24 | v1.15 | P4-03 崩溃恢复：targets.json、窗口校验还原、孤儿 Edge/profile 清理；测试 17/17；source 0.3.0-dev（未发布） |
| 2026-10-24 | v1.16 | P1-03 挂起降级：CAPTURE_TIMEOUT 区分、2 次超时→TARGET_HUNG、快速失败；测试 18/18；per-target worker 仍待做 |
| 2026-10-24 | v1.17 | 挂起检测修正：IsHungAppWindow 前置于 Describe（Describe 会清标记），真实挂起直接拒绝，不再返回旧帧；测试 19/19 |
| 2026-10-24 | v1.18 | P1-03 收口（attach 拒绝挂起；per-target worker 决定不做）；WGC 可见单帧与多屏负坐标布局测试；21/21 |
| 2026-10-24 | v1.19 | P4-02 首版：loopback 状态端点（token/status/panic/status.json）、MCP 内启动、无头 Edge 渲染验证；测试 23/23 |
| 2026-10-24 | v1.20 | 0.3.0-beta：崩溃恢复、挂起拒绝、状态页缩略图、托盘、soak harness（3 分钟 0 失败）；发布包 172 MB |
| 2026-10-24 | v1.21 | DPI awareness 元数据（list_targets/DTO + DPI_UNAWARE warning）、P1-12 安静重测通过、官方 MCP SDK 客户端联调通过；测试 24/24；0.3.0-beta 重打包（SHA256 4F845891…CE17042） |
| 2026-10-24 | v1.22 | 0.3.1-beta：attach_cdp（外部 Chromium/Electron，21 工具）、TARGET_CLOSED 死目标保护、WGC 连续帧；测试 27/27；包 SHA256 5627686F…4FE58D5 |
| 2026-10-24 | v1.23 | 技术实现与限制报告（doc/16）：架构/实测结论/实现逻辑/验证证据/未完成项与所需支持 |
| 2026-10-24 | v1.24 | 0.4.0-beta：DPI-unaware 捕获修复、托盘 per-target 列表/关闭、真实 VS Code attach、install.ps1；测试 28/28；5 分钟 4 目标 soak 0 失败；包 SHA256 1A87DC63…FCBC225 |
| 2026-10-24 | v1.25 | 0.4.1-dev 焦点安全：UIA 动作会激活 WS_EX_NOACTIVATE 停泊窗口；原生元素动作改消息优先，无 HWND 时 UIA 自动执行 + FocusGuard 恢复（strict 可拒绝）；WPF 回退实测；测试夹具屏外、WGC/WPF 测试 opt-in；测试 29/29 |
| 2026-10-24 | v1.26 | 0.5.0-dev Human Consent Gate：默认 strict；USER_ACTIVE/AWAITING_USER/USER_DENIED + consent_id；15s 倒计时非激活托盘窗；状态端点 consent；32/32 |
