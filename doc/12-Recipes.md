# Recipes：应用与网页启动配方

> 适用版本：0.2.2-beta。目标不是“支持一切应用”，而是把常用场景的**能做什么、怎么开、哪里会降级**写清楚。
> 所有配方都遵循同一原则：不抢人的前台/光标/剪贴板；移动窗口必须显式同意；不注入 DLL、不挂钩 API。

## 1. 网页：无头 Edge（默认，推荐）

```text
open_web { url: "http://localhost:3000", width: 1280, height: 800 }
screenshot { target_id }
close_target { target_id }
```

- 真正零窗口：私有 Edge 进程 + 独立临时 profile，不碰用户的浏览器配置。
- 目标 URL 不进入命令行；只允许 http/https/file/about；导航走 CDP。
- 关闭 target 时 `Browser.close` → 杀进程树 → 删 profile。
- 适合：本地前端、设计验收、表单流程、console/network 诊断。

## 2. 网页：用户自己的 Edge / Chrome 窗口

```text
list_windows
attach_window { hwnd: "0x1234", confirm: true }
screenshot { target_id }
```

- `attach_window` 会把窗口移到虚拟屏之外；人看不到，但窗口仍是“显示”状态。
- PrintWindow 能拿到浏览器 chrome + 页面内容（P1-11 实测）。
- UIA 可用；坐标点击/滚动/按键会被 `InputRouter` 拒绝（`INPUT_UNSUPPORTED`），因为 Chromium 忽略窗口消息。
- 需要完整输入能力时，改用 `open_web` 开独立实例，不要接管用户正在使用的浏览器。
- 不要求用户关闭浏览器；但接管会把窗口移走，必须 `confirm=true`。

## 3. VS Code / Electron

- PrintWindow 只拿到窗口 chrome，页面内容为空；**必须用 CDP**。
- 一种可行方式：用独立 profile 启动 VS Code 并开调试端口（由用户手动执行，GhostScreen 不代跑任意命令）：

```powershell
& "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe" `
  --user-data-dir "$env:TEMP\gs-vscode-profile" `
  --remote-debugging-port 9222 --new-window
```

- 当前 MCP 用 `attach_cdp { port: 9222, page_url: "workbench" }` 连接这个端口；`screenshot/observe/click/set_text/key` 与 `open_web` 的 web target 相同。
- `close_target` 只断开 CDP，不关闭 VS Code；外部应用被杀后操作返回 `TARGET_CLOSED`。
- 如果不想开调试端口，VS Code 只能做 PrintWindow/UIA 级别的观测，不能承诺页面内输入。

## 4. 计算器 / 设置（UWP）

- PrintWindow + UIA 完整（P1-11 实测）。
- 启动时可能抢前台，焦点隔离不稳定；需要显式同意，或接受偶发的前台变化。
- 不要把 UWP 当作“绝对不抢焦点”的 target；对焦点敏感的场景优先用 `open_web` 或 WinForms/Win32 应用。

## 5. 本地前端开发循环

前提：dev server 由用户/CI 启动。GhostScreen 不负责跑任意 shell 命令。

```text
open_web { url: "http://localhost:5173", width: 1440, height: 900 }
snapshot { target_id }
console { target_id }
network { target_id }

// 改代码、等 HMR 后
screenshot { target_id }
console { target_id }
network { target_id }

close_target { target_id }
```

- `snapshot` 默认 390×844@2 / 768×1024 / 1440×900，一次拿到三张图和溢出诊断。
- 溢出诊断会给出最多 5 个越界元素（selector/bounds/right），直接定位是哪个组件撑破布局。
- `console` 拿 JS 错误/警告；`network` 拿失败请求与 4xx/5xx。

## 6. 响应式验收

```text
snapshot { target_id }
snapshot { target_id, viewports: [
  { width: 320, height: 568, mobile: true },
  { width: 768, height: 1024 },
  { width: 1280, height: 800 }
] }
```

- 每张图对应 `shots[i].index`；text JSON 里带 `horizontal_overflow`、`scroll_width`、`inner_width`、`hash`。
- 移动端超宽内容会触发 shrink-to-fit，`innerWidth` 可能大于请求宽度；此时以实际 `innerWidth` 判断溢出，不能假定它等于 `width`。
- 整页视图：`screenshot { target_id, full_page: true }`；超长页面受 16384px 限制，用 `clip` 分段。

## 7. 经典 Win32 / WinForms / WPF

```text
list_windows
attach_window { hwnd: "0x1234", confirm: true }
screenshot { target_id }
observe { target_id }
click_element { target_id, ref: "e1" }
set_text { target_id, automation_id: "txtInput", text: "hello 世界" }
close_target { target_id }
```

- PrintWindow 离屏实时；客户区裁剪是默认。
- UIA 优先；坐标点击/滚动/拖拽/按键走窗口消息，不移动真实光标。
- DPI-unaware 遗留应用可能出现逻辑尺寸 + 黑边；列入 P4，不在 v0.2 支持矩阵内。

## 8. Notepad（现代版）

- 启动后如果最小化，attach 会读取 `rcNormalPosition` 并恢复再停泊。
- 现代 Notepad 在 `ShowWindow` 时会重置位置；ParkingManager 有 3 次重试。
- 文本写入优先 `set_text`（UIA）；不行再用 `key` 输入（消息级，复杂 IME 需应用级验证）。

## 9. 兼容矩阵（0.2.2-beta）

| 目标 | 捕获 | 观测 | 输入 | 结论 |
|---|---|---|---|---|
| 无头 Edge（`open_web`） | CDP 视口/整页/裁剪 | DOM | CDP 全套 | ✅ 主力网页方案 |
| Win32 / WinForms / WPF | PrintWindow | UIA | UIA + 窗口消息 | ✅ 主力原生方案 |
| Edge GUI | PrintWindow 完整 | UIA | 仅 UIA | ⚠️ 坐标/输入用 `open_web` |
| VS Code / Electron | 仅 chrome | UIA 有限 | 仅 UIA | ⚠️ 需 CDP 接入（后续） |
| Calculator / 设置（UWP） | PrintWindow | UIA | UIA | ⚠️ 焦点不稳定 |
| DPI-unaware 遗留应用 | PrintWindow（黑边） | UIA | 有限 | ❌ v0.2 不支持 |

## 10. 状态页与托盘（人类观察/急停）

- MCP 启动后会在 stderr 打印状态页 URL，并写 `%LOCALAPPDATA%\GhostScreen\status.json`；浏览器打开该 URL 可看到 target 卡片、缩略图与恢复摘要。
- `GhostScreen.Tray.exe`（发布包 `tray/`）是独立小进程：每 2 秒轮询同一端点，tooltip 显示版本/target 数；菜单可打开状态页、急停（确认后 close_all）、退出。
- 托盘不持有 target：MCP 退出后显示“未运行”；急停只关闭当前 MCP 的 target。
- token 每次启动变化；直接用旧的 URL 会 401，重新从 stderr 或 `status.json` 取。

## 11. 安全与边界

- `attach_window` 会移动窗口，必须 `confirm=true`；不提供“静默接管”。
- 默认不使用 `SendInput`；不动真实光标、不抢前台、不碰剪贴板。
- `open_web` 只允许 http/https/file/about；拒绝 `-` 开头与未知 scheme，避免浏览器参数注入。
- 不注入 DLL、不挂钩 API、不安装驱动、不收集遥测、不联网上报。
- 截图默认只在内存中，写入磁盘由调用方显式指定。
