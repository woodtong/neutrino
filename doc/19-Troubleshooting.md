# Troubleshooting（排障）

> 采集环境诊断：`gs doctor`（文本报告）／`gs doctor --json`（机器可读）／`gs doctor --bundle out.txt`（脱敏后可贴 issue）。
> 本文按现象组织；每个错误码的完整列表见 [03-接口设计](03-接口设计.md)。

## 快速自检

| 现象 | 首先看 | 下一步 |
|---|---|---|
| MCP 工具全部不可用 | `gs doctor` 的 `os` / `status` 项 | OS 低于 Win10 19041 不支持；无 status.json 说明 MCP 未被客户端启动 |
| `open_web` 报 `EDGE_NOT_FOUND` | doctor `edge` 项 | 安装 Edge 到默认路径，或按 [12-Recipes](12-Recipes.md) 用其它 Chromium |
| 截图全黑/空白 | doctor `wgc` 项 + 元数据 `blank` | 最小化/隐藏窗口返回 `NOT_RENDERABLE` 是预期行为；DPI-unaware 见下 |
| 截图内容只占一角（黑边） | 元数据 `dpi` 与 `DPI_UNAWARE` warning | 0.4.0 已修复（unaware 线程上下文 + 物理尺寸缩放）；仍复现请附 doctor bundle |
| 停泊窗口未还原 | doctor `state_dir` 项 | 重启 MCP 会校验 hwnd/pid/类名后还原；损坏 journal 自动隔离为 `.corrupt-*` |
| 状态页打不开 | doctor `status` 项 | status.json 失效则删除并重连；token 在文件内，URL 已带 |
| WPF 按钮点不动 | 错误码 `USER_ACTIVE` / `AWAITING_USER` | 人在使用电脑；按 `retry_after_ms` 重试，或经托盘协商窗允许 |
| VS Code 截图只有边框 | [12-Recipes](12-Recipes.md) VS Code 节 | Electron 需 `attach_cdp`（调试端口），PrintWindow 拿不到页面内容 |

## 常见错误码

| 错误码 | 含义 | 处理 |
|---|---|---|
| `CONFIRM_REQUIRED` | `gs shot` 要移动他人窗口 | 加 `--confirm` |
| `NOT_RENDERABLE` | 窗口最小化/隐藏/无 bounds | 先恢复窗口再截图 |
| `CAPTURE_TIMEOUT` | PrintWindow 6s 超时 | 应用挂起或忙；连续 2 次后 target 降级，close 仍可还原 |
| `TARGET_HUNG` | `IsHungAppWindow` 判定挂起 | 等应用响应；attach 前即拒绝挂起窗口 |
| `USER_ACTIVE` | 人在使用电脑（strict 门禁） | 按 `retry_after_ms` 重试；3 次后走协商窗 |
| `AWAITING_USER` | 等待人批准 `consent_id` | 托盘协商窗允许/拒绝；15s 未决策自动放行 |
| `USER_DENIED` / `CONSENT_EXPIRED` | 拒绝或票过期 | 重新发起动作 |
| `EDGE_NOT_FOUND` | 未找到 Edge 安装 | 见上表 |
| `CDP_CONNECT_FAILED` | DevTools 端口连不上 | 确认 `--remote-debugging-port` 与端口文件 |
| `TARGET_CLOSED` | 浏览器/窗口已退出 | 正常生命周期错误；重新 open |
| `STATE_WRITE_FAILED` | journal 写入失败 | 检查 `%LOCALAPPDATA%\GhostScreen` 磁盘/权限 |
| `INPUT_UNSUPPORTED` | Chromium/Electron 不收窗口消息 | 走 CDP 路径（open_web/attach_cdp） |

## 无人值守 / 服务环境

- 断开的 RDP 会话没有桌面 → `gs doctor` 的 `display` 项会报虚拟屏为空；保持交互会话或使用 console 会话。
- 计划任务里跑 MCP 需允许与桌面交互，否则枚举不到窗口。

## 提 issue 前

```
gs doctor --bundle doctor.txt
```

bundle 已脱敏（用户名 → `%USER%`，token → `%REDACTED%`），连同复现步骤与错误码一起贴出。
