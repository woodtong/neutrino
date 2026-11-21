using System.Globalization;

namespace GhostScreen.Core;

/// <summary>
/// Every word the notifier can put in front of a human, in one place: the consent
/// prompt, the toasts, and the fallback button. The rest of the product is Chinese
/// only; notifications are different because they interrupt someone who may not
/// read Chinese at all, so they follow the system UI language and fall back to
/// English when the system is not Chinese.
/// </summary>
public static class NotifierText
{
    private static string? _override;

    /// <summary>
    /// <c>zh</c> or <c>en</c>. Defaults to <c>GHOSTSCREEN_NOTIFY_LANG</c>, then to the
    /// system UI language; tests pin it so assertions never depend on the machine.
    /// </summary>
    public static string Language
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_override))
            {
                return _override;
            }

            var configured = Environment.GetEnvironmentVariable("GHOSTSCREEN_NOTIFY_LANG");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim().ToLowerInvariant();
            }

            return string.Equals(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                "zh",
                StringComparison.OrdinalIgnoreCase)
                ? "zh"
                : "en";
        }

        set => _override = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    public static bool IsChinese => Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    // ---- consent prompt -------------------------------------------------

    public static string ConsentTitle => IsChinese ? "智能体请求打断你" : "An agent asks to interrupt you";

    public static string ConsentLine1(string agent, string target, string action) => IsChinese
        ? $"{agent} 想在「{target}」上{action}"
        : $"{agent} wants to {action} in \"{target}\"";

    public static string ConsentLine2 => IsChinese
        ? "这一步必须先取得前台焦点，等于打断你正在做的事"
        : "This step has to take the foreground, which interrupts what you are doing";

    public static string ConsentLine3(string activity) => IsChinese
        ? $"你正在：{activity}"
        : $"You are: {activity}";

    public static string ConsentLine4(string verb) => IsChinese
        ? $"若同意，请先停下手上的{verb}；一秒后焦点自动还给你"
        : $"If you agree, pause your {verb} for a second; the focus comes back right after";

    public static string AskAction(string action) => action switch
    {
        "click_element" => IsChinese ? "点击一个控件" : "click a control",
        "set_text" => IsChinese ? "往输入框里写字" : "type into a field",
        "click" => IsChinese ? "点击某个位置" : "click a point",
        _ => action,
    };

    public static string ActivityText(string activity, string? detail) => activity switch
    {
        "typing" => IsChinese
            ? $"「{detail}」里输入文字"
            : $"typing in \"{detail}\"",
        "pointing" => IsChinese
            ? $"「{detail}」里点鼠标或操作菜单"
            : $"clicking in \"{detail}\"",
        _ => IsChinese ? "使用桌面" : "using the desktop",
    };

    public static string ActivityVerb(string activity) => activity switch
    {
        "typing" => IsChinese ? "输入" : "typing",
        "pointing" => IsChinese ? "点击" : "clicking",
        _ => IsChinese ? "操作" : "work",
    };

    public static string AnotherWindow => IsChinese ? "另一个窗口" : "another window";

    public static string ActionDeny => IsChinese ? "拒绝" : "Deny";

    public static string ActionDefer => IsChinese ? "延时" : "Defer";

    public static string ActionAllow => IsChinese ? "允许打断" : "Allow";

    public static string Countdown(string? deferrals) => IsChinese
        ? deferrals is null
            ? "若未操作，{0} 秒后自动放行这次打断"
            : $"若未操作，{{0}} 秒后自动放行这次打断（已延时 {deferrals}）"
        : deferrals is null
            ? "Allowing this interruption in {0} s if you do nothing"
            : $"Allowing this interruption in {{0}} s (deferred {deferrals})";

    // ---- toasts ---------------------------------------------------------

    public static string DeferLimitReached => IsChinese
        ? "The deferral limit is reached for this ticket."
        : "The deferral limit is reached for this ticket.";

    public static string StillWaitingTitle => IsChinese ? "智能体正在等你" : "The agent is waiting for you";

    public static string StillWaitingBody(string target) => IsChinese
        ? $"「{target}」的操作被你的输入挡住了；你停手约两秒它就会继续，不需要做别的。"
        : $"Your typing is holding up the agent on \"{target}\"; pause for about two seconds and it continues. Nothing else is needed.";

    public static string DegradedTitle => IsChinese ? "窗口无响应" : "Window is not responding";

    public static string DegradedBody(string target) => IsChinese
        ? $"「{target}」连续没有响应，截图/观测会降级；必要时请重开这个应用。"
        : $"\"{target}\" stopped responding; capture and observation are degraded. Restart that app if needed.";

    public static string CloseRefusedTitle => IsChinese ? "有个窗口没能关掉" : "A window would not close";

    public static string CloseRefusedBody(string target) => IsChinese
        ? $"「{target}」拒绝了关闭请求（可能在等你保存），已经还原到桌面上，你可以自己处理。"
        : $"\"{target}\" refused to close (it may be waiting for a save). It is back on your desktop for you to handle.";

    public static string RecoveryTitle => IsChinese ? "上次异常退出已恢复" : "Recovered from the last exit";

    public static string RecoveryBody(int restored, int browsers, int skipped)
    {
        if (IsChinese)
        {
            return $"已还原 {restored} 个窗口"
                + (browsers > 0 ? $"，清理 {browsers} 个浏览器" : string.Empty)
                + (skipped > 0 ? $"，{skipped} 个窗口已不存在" : string.Empty)
                + "。";
        }

        return $"Restored {restored} window(s)"
            + (browsers > 0 ? $", closed {browsers} browser(s)" : string.Empty)
            + (skipped > 0 ? $", {skipped} no longer exist" : string.Empty)
            + ".";
    }

    public static string DownTitle => IsChinese ? "中微子已断开" : "GhostScreen disconnected";

    public static string DownBody => IsChinese
        ? "Agent 的 MCP 进程不再响应。它停靠的窗口会在下次启动时还原，你也可以在托盘里急停。"
        : "The agent's MCP process stopped responding. Its parked windows come back on the next start; you can also stop it from the tray.";

    public static string FatalTitle => IsChinese ? "中微子出错了" : "GhostScreen failed";

    public static string FatalBody(string error) => IsChinese
        ? $"MCP 进程遇到未处理的异常：{error}"
        : $"The MCP process hit an unhandled error: {error}";

    public static string Dismiss => IsChinese ? "知道了" : "OK";
}
