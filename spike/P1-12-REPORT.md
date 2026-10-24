# P1-12 安静环境不变量测试

> 日期：2026-10-24
> 目标：在无人类输入的窗口内，严格验证停泊 + PrintWindow + UIA 不改变前台、光标、剪贴板。
> 证据：`spike/evidence/quiet.json`。

## 方法

`dotnet run --project spike/GhostScreen.Spike -c Release -- --quiet`

1. 等待 `GetLastInputInfo` 连续 3 秒不变（最多 20 秒）。
2. 记录前台窗口、光标位置、剪贴板序列号、最后输入 tick。
3. 在屏幕外创建 FixtureApp → 停泊 → `RedrawWindow`。
4. UIA Invoke「Click Me」+ SetValue 输入框「quiet 世界」。
5. PrintWindow 连续 3 次。
6. 关闭 / 还原，再次记录四项状态。

## 结果（两轮）

| 轮次 | quiet | foregroundOk | cursorOk | clipboardOk | humanInput | pass |
|---|---|---|---|---|---|---|
| 1 | true | false | true | true | true | ❌ |
| 2 | true | true | true | true | true | ❌ |

- 第 1 轮前台从 `0x5104C` 变为 `0x2088C`，同时 `lastInput` 变化；无法区分是 UIA 激活还是人类切换。
- 第 2 轮前台/光标/剪贴板完全不变，但 `lastInput` 变化 → 人类在测试期间有输入，严格 pass 不能成立。
- `printWindowHashes` 每轮 3 个不同 hash，画面是活的。
- UIA Invoke/SetValue 均成功（`uiaOk=true`）。

## 结论

- 测试工具正确；当前环境不安静，**不能用这两轮宣称光标/前台不变量严格通过**。
- 需要在一个真正无人操作的时间窗重跑 `--quiet`；如果仍出现 `humanInput=true`，则延长等待或改用合成输入监控。
- 产品文档必须区分"无人类输入时通过"和"有人类输入时的污染结果"。

## 下一步

- 找无人操作窗口重跑，保存 `quiet.json` 作为 G1 证据。
- 若仍不稳定，考虑将测试放到 self-hosted runner / 专用测试机。
