# MCP 客户端配置

> 适用于 v0.2 的 MCP stdio 服务器（18 工具，原生窗口 + 无头网页）。当前实现是**单进程**：target 由该 MCP 进程持有，断连/EOF 还原。
> 可执行文件（先构建）：
> `src\GhostScreen.Mcp\bin\Release\net8.0-windows10.0.19041.0\GhostScreen.Mcp.exe`

## 1. 通用配置

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

要求：

- Windows 11，MCP 客户端与目标窗口在同一交互式用户会话。
- 不需要管理员权限；提权窗口不支持。
- stdout 只用于 JSON-RPC，诊断日志在 stderr。

## 2. Claude Code

```bash
claude mcp add ghostscreen -- "E:\software_code\Visual_Stdio_Code\WindowsForAgent\src\GhostScreen.Mcp\bin\Release\net8.0-windows10.0.19041.0\GhostScreen.Mcp.exe"
```

然后在 Claude Code 里 `/mcp` 查看 `ghostscreen` 是否连接。若你的 Claude Code 版本使用配置文件，按通用 JSON 写入即可。

## 3. Codex

`~/.codex/config.toml`：

```toml
[mcp_servers.ghostscreen]
command = "E:\\software_code\\Visual_Stdio_Code\\WindowsForAgent\\src\\GhostScreen.Mcp\\bin\\Release\\net8.0-windows10.0.19041.0\\GhostScreen.Mcp.exe"
args = []
```

## 4. Cursor

项目级 `.cursor/mcp.json`（或用户级 MCP 配置）：

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

## 5. ZCode / 其他 MCP 客户端

在客户端的 MCP 设置里新增一个 **stdio** server：

- command：上面的 `GhostScreen.Mcp.exe` 绝对路径
- args：空
- transport：stdio

不同客户端字段名可能不同（`command` / `args` / `mcpServers`），但本质都是启动该 exe 并连 stdin/stdout。

## 6. 典型工作流

```text
1. list_windows
   → 找到目标窗口的 hwnd（例如 0x1234）

2. attach_window { hwnd: "0x1234", confirm: true }
   → target_id，窗口停泊到屏幕外

3. screenshot { target_id }
   → PNG image content + backend/width/height/stable/hash/dpi 元数据
   → 默认是客户区裁剪，图像坐标 = 客户区坐标

4. observe { target_id }
   → { epoch, elements: [{ refId, role, name, automation_id, bounds, patterns }] }

5. click_element { target_id, ref: "e1" }    // 稳定，优先；默认走 HWND 消息
   或
   click { target_id, x: 420, y: 33 }         // 像素坐标，经典应用

6. set_text { target_id, automation_id: "txtInput", text: "hello 世界" }
   // 默认 WM_SETTEXT 到 EDIT/RICHEDIT 子控件；WPF/UWP 等无 HWND 控件会自动走 UIA
   // 并恢复前台。需要绝对零激活时加 strict_no_focus_steal: true（会拒绝）。

7. close_target { target_id }                  // 还原
   close_all                                   // 急停/全部还原
```

### 6.1 网页目标（0.2.0-p0）

```text
1. open_web { url: "https://example.com", width: 1280, height: 800 }
   → target_id（kind=web），私有 headless Edge，用户看不到窗口

2. screenshot { target_id }
   → PNG；backend=cdp；width/height 是设备像素
   → css_width/css_height 是 CSS 像素；坐标用 CSS 像素

3. observe { target_id }
   → { epoch, elements: [{ refId, role, name, selector, bounds, enabled }] }

4. click_element { target_id, ref: "e3" }      // 滚动/布局变化后仍重新取中心
   set_text     { target_id, ref: "e2", text: "hello 世界" }
   key          { target_id, keys: ["CTRL","S"] }
   scroll       { target_id, x: 400, y: 300, delta: -120 }
   drag         { target_id, from_x: 100, from_y: 200, to_x: 300, to_y: 200 }

5. viewport { target_id, width: 390, height: 844, device_scale_factor: 2, mobile: true }
   navigate { target_id, url: "https://example.com/pricing" }
   console  { target_id, limit: 50 }
   network  { target_id, limit: 50 }        // 失败请求与 4xx/5xx
   get_html { target_id, selector: "main" }

6. screenshot { target_id, full_page: true }          // 整页
   screenshot { target_id, clip: { x: 0, y: 0, width: 800, height: 600 } }
   snapshot   { target_id }                            // 默认 390/768/1440，多图 + 溢出诊断
   snapshot   { target_id, viewports: [{ width: 400, height: 800 }] }

7. close_target { target_id }                  // Browser.close + 杀进程树 + 删 profile
```

`snapshot` 返回顺序：一段 text JSON（`shots[].viewport/width/height/hash/overflow`）+ 每个视口一张 PNG；`overflow.offenders` 给出越界元素 selector 与 bounds。

注意：web 截图是设备像素；DPR=2 时 390×844 视口截图是 780×1688。`click`/`scroll`/`drag` 的坐标始终是 **CSS 像素**。

### 6.2 已有 Chromium/Electron（VS Code）

```text
1. 用户/脚本以调试端口启动应用（GhostScreen 不代跑命令）：
   Code.exe --user-data-dir %TEMP%\gs-vscode --remote-debugging-port 9222 --new-window

2. attach_cdp { port: 9222, page_url: "workbench" }
   → target_id（kind=web, external=true）

3. screenshot / observe / click_element / set_text / key / console ...
   → 与 open_web 的 web target 相同

4. close_target { target_id }
   → 只断开 CDP 连接，不关闭 VS Code
```

连接失败返回 `CDP_CONNECT_FAILED`；外部浏览器被杀后操作返回 `TARGET_CLOSED`。

## 7. 能力边界

| 目标 | 捕获 | 观测 | 输入 | 备注 |
|---|---|---|---|---|
| 经典 Win32 / WinForms | PrintWindow | UIA | 消息 click/scroll/drag/key/set_text；UIA 仅观测 | 主力场景；元素动作不激活窗口 |
| WPF | PrintWindow | UIA | 无子 HWND；默认 strict：人在用时先 `USER_ACTIVE`，3 次后协商 | 消息点击无效，必须 UIA/协商 |
| Edge GUI（Chromium） | PrintWindow | UIA | CDP（`open_web`/`attach_cdp`）；UIA 回退自动执行并恢复 | 消息输入会被拒绝 |
| VS Code（Electron） | PrintWindow 只有 chrome | UIA 有限 | CDP；消息输入拒绝 | 用 `--remote-debugging-port` 启动后 `attach_cdp { port }` |
| Calculator（UWP） | PrintWindow | UIA | UIA（默认 strict，需协商） | 启动可能抢焦点 |
| 网页（`open_web`） | 无头 Edge + CDP | DOM | CDP | 真正零窗口，独立临时 profile |
| 已有 Chromium/Electron（`attach_cdp`） | CDP | DOM | CDP | 不启动/不关闭外部浏览器；close 只 detach |

- 最小化、隐藏、cloak、DRM 窗口不支持。
- `attach_window` 会移动窗口，必须 `confirm=true`。
- `click` 坐标点击：有可点击 HWND 子控件走消息；WPF/UWP 单 HWND 用 UIA `AtPoint`。`click_element`/`set_text` 先用 UIA bounds 找 HWND 子控件走消息；没有 HWND 时走 UIA。默认 strict：人在用时返回 `USER_ACTIVE`/`AWAITING_USER`，Agent 带 `consent_id` 重试。动作返回 `focus: not-needed|restored|stolen`。
- 默认不使用 `SendInput`。

## 8. 故障排查

- **客户端看不到工具**：确认 command 是绝对路径、exe 能独立运行、stdio 没有把日志写到 stdout。
- **attach 返回 CONFIRM_REQUIRED**：加 `"confirm": true`。
- **INPUT_UNSUPPORTED**：目标是 Chromium/Electron；改用 `observe` + `click_element` ref，或 CDP。
- **USER_ACTIVE**：人在用电脑，strict 门禁挡住 UIA 动作；按 `retry_after_ms` 重试，3 次后会出现协商。
- **AWAITING_USER**：协商请求已创建；用返回的 `consent_id` 重试同一动作（允许或 15 秒倒计时后）。
- **USER_DENIED**：自然人拒绝；本次终止，不要立即重试。
- **CONSENT_EXPIRED**：consent_id 过期/不匹配；重新发起动作获取新票。
- **FOCUS_STEAL_NOT_ALLOWED**：传了 `strict_no_focus_steal=true` 且动作需要 UIA；关闭该参数或改用坐标/消息动作。
- **ELEMENT_NOT_ACTIONABLE**：控件没有可执行 pattern；换控件或改用坐标点击。
- **FOCUS_STOLEN**：动作已完成但前台没恢复；不要盲目重试，提示用户点回原窗口。
- **ELEMENT_STALE**：重新 `observe` 获取新 ref。
- **NOT_RENDERABLE**：窗口最小化/隐藏/cloak；恢复后重试。
- **截图全黑**：目标应用不支持 PrintWindow；考虑 VDD/可见窗口/CDP。
- **退出后窗口没还原**：正常 EOF 会还原；进程被强杀不会（后续 watchdog）。
