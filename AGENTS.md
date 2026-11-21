# AGENTS.md — 中微子（GhostScreen）仓库入口

> 给**下一个接手的 agent** 读的第一份文件。读完本文就知道：这个项目是干什么的、功能散在哪、动完代码该改哪些文档。
>
> 状态快照（版本、测试数、下一步）在 `CONTEXT.md`，**会频繁变**；本文只写**稳定边界与规则**，尽量少改。
> 最后核对：2026-11-21（开发线 `0.6.0-dev`，对外 Release v0.2.0）。

---

## 1. 一句话

**给 Agent 一块人类看不见的屏幕。** 应用窗口在系统里照常真实渲染，但不出现在人的显示器上；Agent 可以随时截图、观测、点击、输入，人继续用自己的电脑——不闪窗、不被抢焦点、不被抢鼠标。

**不是什么**（避免方向跑偏）：不是云桌面 / RDP / RPA 平台 / 浏览器插件 / 内核驱动 / 跨平台工具 / 商业产品（Apache-2.0 开源）。

---

## 2. 命名谱系（四个名字 = 一个东西）

| 层 | 名字 | 出现的地方 |
|---|---|---|
| 口头代号 | **北极星** | 用户日常怎么称呼它（不进仓库） |
| 产品/品牌 | **中微子（Neutrino）** | README 标题、文档标题、GitHub 仓库 `woodtong/neutrino`、托盘菜单“中微子 未运行” |
| 内部代号 | **GhostScreen** | exe 名、命名空间、MCP server key `ghostscreen`、`%LOCALAPPDATA%\GhostScreen\` |
| 本地目录 | **WindowsForAgent** | 本仓库目录名与 `GhostScreen.sln` 所在位置 |

**GhostScreen 这个名字的来由**就是产品本身：一块“幽灵屏幕”（Ghost + Screen）。技术层**冻结不改名**——改名会破坏已有客户端配置（MCP key、配置路径），要动必须单独走技术重命名（见 `doc/05`）。

---

## 3. 功能地图（能力 → 工具 → 源码 → 文档）

| 能力 | MCP 工具 | 源码 | 详细文档 |
|---|---|---|---|
| 窗口枚举/停泊到屏外 | `list_windows`、`attach_window`、`park_window`、`show_to_user`、`set_visibility` | `src/GhostScreen.Windows` | `doc/02`、`doc/03` |
| 真实渲染截图 | `screenshot` | `src/GhostScreen.Capture`（PrintWindow 主路径 + WGC 可见） | `doc/01`、`doc/16` |
| 结构化观测 | `observe`、`snapshot` | `src/GhostScreen.Input`（UIA）、`src/GhostScreen.Web`（DOM） | `doc/03` |
| 原生输入（不抢焦点） | `click`、`click_element`、`scroll`、`drag`、`key`、`set_text` | `src/GhostScreen.Input` + `src/GhostScreen.Daemon/InputRouter` | `doc/03` |
| 网页（无头 Edge） | `open_web`、`navigate`、`viewport`、`get_html`、`console`、`network` | `src/GhostScreen.Web` | `doc/03`、`doc/12` |
| Electron/CDP 接入 | `attach_cdp` | `src/GhostScreen.Web/Cdp*` | `doc/03` |
| 人在用时的人工协商 | `USER_ACTIVE` / `AWAITING_USER` / `USER_DENIED` + `consent_id` | `src/GhostScreen.Daemon/ConsentManager` | `doc/02`、`doc/16` |
| 一轮结束归还 | `end_turn`、`close_target`、`close_all` | `src/GhostScreen.Daemon` | `doc/03` |
| 状态页 / 托盘 / 急停 | HTTP 状态端点 + `GhostScreen.Tray.exe` | `src/GhostScreen.Daemon/StatusServer`、`src/GhostScreen.Tray` | `doc/02`、`doc/19` |
| 崩溃恢复 / 挂起拒绝 | 自动（journal → 下次启动还原） | `src/GhostScreen.Daemon/TargetJournal` | `doc/02`、`doc/16` |
| CLI 与自检 | `gs version/state/list/shot/doctor` | `src/GhostScreen.Cli` | `doc/19` |

**25 个 MCP 工具**的完整签名与错误码见 `doc/03-接口设计.md`；客户端接入示例见 `doc/08-MCP客户端配置.md`。

### 进程与本地目录

| 进程 | 谁启动 | 作用 |
|---|---|---|
| `GhostScreen.Mcp.exe` | MCP 客户端（如 DSH 插件、其他客户端） | **唯一的服务进程**：stdio MCP + 内嵌 `GhostScreen.Daemon`（库）+ 状态 HTTP 端点 |
| `GhostScreen.Cli.exe` | 人 | 一次性命令（list/shot/doctor） |
| `GhostScreen.Tray.exe` | 人 | 可选托盘 UI，轮询状态端点；宿主通知管道服务（协商窗与故障提示） |
| `GhostScreen.Notifier.exe` | 人（或 MCP 按需拉起） | 独立通知宿主：托盘在跑时由托盘内嵌同一服务，托盘不在时由客户端拉起（MCP 崩溃后仍能弹窗） |

> **没有独立 Windows 服务、没有计划任务、没有注册表自启。** daemon 是 `.dll` 库，嵌在 MCP 进程里。
> 用户态数据在 `%LOCALAPPDATA%\GhostScreen\`：`status.json`、`state\`（含崩溃恢复 journal）、`web-profiles\`（无头 Edge 临时 profile）。**这些是用户数据，任何更新/清理都不能删。**

---

## 4. 构建与验证（收工前必须绿）

```powershell
dotnet build GhostScreen.sln -c Release                          # 0 warning / 0 error
dotnet run --project tests/GhostScreen.Tests -c Release          # 当前记录 47/47
dotnet format --verify-no-changes
```

可选压力：`tests/GhostScreen.Soak`、`tests/GhostScreen.WebStress`（见 `CONTEXT.md` §4）。
`TreatWarningsAsErrors=true`，所以“警告”就是失败。测试需要真实桌面会话，**不要伪造通过**；检测到人在用电脑时，部分焦点断言会跳过（这是设计，不是失败）。

---

## 5. 发布链路（改变用户端行为的唯一动作）

- **开发线**版本在 `Directory.Build.props`（当前 `0.6.0-dev`）；**发布线**从 `0.1.0` 起独立编号。
- `scripts/publish.ps1` 产出 `artifacts/GhostScreen-<ver>-win-x64.zip` + `.sha256`，包内布局：

```
GhostScreen-<ver>\
├── mcp\GhostScreen.Mcp.exe       ← Agent 接入点
├── cli\GhostScreen.Cli.exe
├── tray\GhostScreen.Tray.exe
├── GhostScreen.Notifier\GhostScreen.Notifier.exe   ← 独立通知宿主（0.2.0 起）
├── docs\ (MCP-CLIENTS.md, RELEASE-NOTES.md)
├── README.md  CHANGELOG.md  LICENSE
└── VERSION                        ← 纯文本版本号（更新器据此判断）
```

- 发布 = GitHub Release（仓库 `woodtong/neutrino`，tag `vX.Y.Z`，附 zip 与 sha256）。
- **下游自动更新器（`dsh-ghostscreen-mcp` 插件）只依赖 4 个契约**，破坏任何一个都会让用户的自动更新失效：

| 契约 | 内容 |
|---|---|
| ① 进程入口 | `mcp\GhostScreen.Mcp.exe` 以 stdio 提供 MCP，server key `ghostscreen` |
| ② 资产形态 | 文件名匹配 `^GhostScreen-[\w.\-]+-win-x64\.zip$`，包内路径如上 |
| ③ 版本号 | `VERSION` 与 tag 均为 `vX.Y.Z[-pre]` 可解析 |
| ④ 宿主插件 API | DSH 侧的 `ctx.plugin(mcpClient,…)`、`plugins.row.config`、Config volatile |

**改这 4 项中的任何一项，必须同步通知插件侧（另一仓库 `dsh-ghostscreen-mcp`）。**

---

## 6. 与 DSH 插件的关系

`dsh-ghostscreen-mcp` 是**另一套代码、另一个仓库**，只做三件事：把本体的 MCP 挂进 DSH、按需下载/更新本体整包、提供配置页。它不包含任何产品逻辑。

- 本仓库只管**产品本身**；不要因为插件需要而改动本体的行为契约。
- 用户端行为由**发 Release** 驱动；不发 Release，插件与用户都不会感知到你的改动。
- 开发机常用“pin 本地构建”（`cordis.patch.yml` 的 `config.exePath`）跑 `bin\Release` 里的开发版，此时更新器只下载不切换。

---

## 7. 文档地图（先读哪个）

| 文档 | 负责什么 | 什么时候读/改 |
|---|---|---|
| `AGENTS.md`（本文） | 稳定边界、功能地图、文档维护规则 | **开工第一份**；改动分层/约定/发布契约时改 |
| `CONTEXT.md` | **当前状态快照**：版本、已验证项、已知限制、下一步 | 开工第二份；**每次收工都要更新** |
| `PLAN.md` | 范围、切片拆分、验收标准 | 领任务/改范围时 |
| `doc/00-项目概览.md` | 定位、要解决的问题、典型场景、非目标 | 讲清“为什么做”时 |
| `doc/01` | 技术选型与可行性实测 | 质疑技术路线时 |
| `doc/02` | 架构设计（进程、目标、输入路由、门禁） | 改结构时 |
| `doc/03` | **接口设计**：25 个工具的入参/出参/错误码 | 改任何 MCP 工具时 |
| `doc/04` | 路线图与验收清单（P0–P4） | 判定“做完了没有” |
| `doc/05` | **决策日志 + 进度流水**（追加，不改历史） | 做决策时；开工读最后一条 |
| `doc/07` | 开源规划（许可证、治理、发布节奏） | 涉及对外承诺时 |
| `doc/08` | 各 MCP 客户端配置示例 | 用户接入报错时 |
| `doc/12` | Recipes（常见任务配方） | 想知道“标准打法”时 |
| `doc/16` | 技术实现与限制报告（最全的“为什么”和“做不到什么”） | 排查复杂问题/写文档时 |
| `doc/17` | 某次发布说明 | 发版时新增一份 |
| `doc/18` | 技术栈清单 | 加依赖/换库时 |
| `doc/19` | Troubleshooting（用户可见症状 → 原因 → 处置） | 修用户可见 bug 时 |
| `doc/20` | 功能 TODO / 候选清单 | 找活干时 |
| `spike/*-REPORT.md` | 一次性验证的证据（截图、数据） | 需要“实测证据”时 |

---

## 8. 什么时候必须改写文档（硬规则）

**原则：文档不是事后整理，是改动的一部分。** 下面每一条都是“改了 A 就必须改 B”，同一批改动里完成，不要留到“以后补”。

| 你做了什么 | 必须更新 |
|---|---|
| 新增 / 删除 / 重命名一个 MCP 工具 | `doc/03`（签名）、`CONTEXT.md` §3（工具清单）、README 的“25 工具”数字、`CHANGELOG.md`；工具用法有变则 `doc/08`/`doc/12` |
| 改了工具行为、默认值、错误码（strict/balanced/force、裁剪、超时、`USER_ACTIVE` 等） | `doc/03`、`CONTEXT.md` §2、`CHANGELOG.md`；用户可见症状变则 `doc/19` |
| 完成一个 PLAN 切片 | `PLAN.md` 打勾、`doc/04` 验收结果、`doc/05` 追加一条、`CONTEXT.md` §3/§6 |
| 做了技术决策 / 否决了某方案 | `doc/05` **追加**一条：日期 + 决策 + 理由 + 证据（不改历史条目） |
| 修了用户可见 bug | `doc/19`（症状→原因→处置）、`CHANGELOG.md`；根因属架构则补 `doc/16` |
| 发现新限制 / 新的“做不到” | `CONTEXT.md` §5、`doc/16`；能排期的进 `doc/20` |
| 新增/删除源码项目或目录 | 本文 §3 功能地图、`CONTEXT.md` §3 |
| 改了发布包布局、`publish.ps1`、版本号规则 | 本文 §5（含 4 个契约）、`CHANGELOG.md`；**并知会 `dsh-ghostscreen-mcp` 仓库** |
| 发版（新 Release） | `CHANGELOG.md`、新增 `doc/17-<ver>发布说明.md`、README 状态行、`CONTEXT.md` |
| 改技术栈 / 加依赖 | `doc/18`、`doc/05`；影响安全则 `SECURITY.md` |
| 会话收工（无论是否完成切片） | `CONTEXT.md`：顶部日期、§4 验证结果、§5 限制、§6 下一步 |
| 想改 MCP key / 包名 / 目录名 | **默认不做**；确需做 → 先写 `doc/05` 决策 + 迁移与兼容方案 + 通知下游 |

**判断标准（一分钟自检）**：如果下一个 agent 只读文档、不读代码，会不会做出错误决定？会 → 必须改文档。

---

## 9. 工作守则

1. **先读再用**：`AGENTS.md`（本文）→ `CONTEXT.md` → `PLAN.md` → `doc/05` 最后一条 → 需要全貌读 `doc/16`。
2. **一次一个垂直切片**：做完、验证、更新文档，再开下一个。
3. **绿了才收工**：build 0 warning、tests 通过、format 通过。
4. **不伪造证据**：不把黑图/陈旧帧当成功，不把跳过当通过，测试结果必须来自真实运行。
5. **不打扰人**：任何动作不得抢前台焦点、不得移动真实光标、不得改剪贴板；做不到就返回错误码，而不是硬来。
6. **隐私**：日志/报告不得包含用户名、token、窗口标题中的敏感内容；`gs doctor --bundle` 已做脱敏，沿用同一标准。
7. **状态与规则分离**：会变的事实写 `CONTEXT.md`，稳定的边界写本文；不要让 `CONTEXT.md` 长出规则，也不要让本文堆满进度。
