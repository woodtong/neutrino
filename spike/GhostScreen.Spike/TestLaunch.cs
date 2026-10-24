using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GhostScreen.Spike;

/// <summary>
/// Test-only launcher. Executable paths and arguments are compile-time constants;
/// no caller can supply a command line. CreateProcess is used directly (no shell).
/// </summary>
internal static class TestLaunch
{
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string EdgeProfile = @"E:\software_code\Visual_Stdio_Code\WindowsForAgent\spike\out\edge-profile";
    private const string EdgeGuiProfile = @"E:\software_code\Visual_Stdio_Code\WindowsForAgent\spike\out\edge-gui-profile";
    private const string VsCodePath = @"D:\办公食用\下载内容\Microsoft VS Code\Code.exe";
    private const string VsCodeProfile = @"E:\software_code\Visual_Stdio_Code\WindowsForAgent\spike\out\vscode-profile";
    private const string EdgeCommandLine =
        "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" " +
        "--headless=new --remote-debugging-port=9222 " +
        "--user-data-dir=E:\\software_code\\Visual_Stdio_Code\\WindowsForAgent\\spike\\out\\edge-profile " +
        "--window-size=1280,800 --force-device-scale-factor=1 --hide-scrollbars " +
        "--no-first-run --no-default-browser-check " +
        "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
        "--disable-background-timer-throttling about:blank";
    private const string EdgeGuiCommandLine =
        "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" " +
        "--user-data-dir=E:\\software_code\\Visual_Stdio_Code\\WindowsForAgent\\spike\\out\\edge-gui-profile " +
        "--window-size=1200,900 --window-position=2700,200 " +
        "--no-first-run --no-default-browser-check " +
        "file:///E:/software_code/Visual_Stdio_Code/WindowsForAgent/spike/testpage/index.html";
    private const string VsCodeCommandLine =
        "\"D:\\办公食用\\下载内容\\Microsoft VS Code\\Code.exe\" " +
        "--user-data-dir=E:\\software_code\\Visual_Stdio_Code\\WindowsForAgent\\spike\\out\\vscode-profile " +
        "--disable-extensions --new-window --skip-welcome --skip-release-notes " +
        "--window-position=2700,200 --remote-debugging-port=9223";

    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const uint STARTF_USEPOSITION = 0x00000004;
    private const uint STARTF_USESIZE = 0x00000008;
    private const short SW_HIDE = 0;
    private const short SW_SHOWNOACTIVATE = 4;
    private const short SW_SHOWMINNOACTIVE = 7;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    public static Process? StartNotepad()
    {
        var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        return StartFixedProcess(notepadPath, notepadPath, 0, SW_HIDE, offscreenShow: false);
    }

    public static Process? StartHeadlessEdge()
    {
        try
        {
            if (Directory.Exists(EdgeProfile))
            {
                Directory.Delete(EdgeProfile, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }

        Directory.CreateDirectory(EdgeProfile);
        return StartFixedProcess(EdgePath, EdgeCommandLine, CREATE_NO_WINDOW, SW_HIDE, offscreenShow: false);
    }

    public static Process? StartEdgeGui()
    {
        ResetProfile(EdgeGuiProfile);
        return StartFixedProcess(EdgePath, EdgeGuiCommandLine, 0, SW_SHOWNOACTIVATE, offscreenShow: true);
    }

    public static Process? StartVsCode()
    {
        ResetProfile(VsCodeProfile);
        return StartFixedProcess(VsCodePath, VsCodeCommandLine, 0, SW_SHOWMINNOACTIVE, offscreenShow: false);
    }

    public static Process? StartCalculator()
    {
        var calculatorPath = Path.Combine(Environment.SystemDirectory, "calc.exe");
        return StartFixedProcess(calculatorPath, calculatorPath, 0, SW_SHOWMINNOACTIVE, offscreenShow: false);
    }

    private static void ResetProfile(string profilePath)
    {
        try
        {
            if (Directory.Exists(profilePath))
            {
                Directory.Delete(profilePath, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }

        Directory.CreateDirectory(profilePath);
    }

    /// <summary>
    /// Private by design: no caller can supply an executable or command line.
    /// Both call sites pass compile-time constants; there is no shell involved.
    /// Static analysis may still flag process creation, but there is no
    /// user/agent-controlled input on this path.
    /// </summary>
    private static Process? StartFixedProcess(
        string applicationName,
        string commandLine,
        uint creationFlags,
        short showWindow,
        bool offscreenShow)
    {
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = showWindow,
        };

        if (offscreenShow)
        {
            startup.dwFlags |= STARTF_USEPOSITION | STARTF_USESIZE;
            startup.dwX = 2700;
            startup.dwY = 200;
            startup.dwXSize = 1200;
            startup.dwYSize = 900;
        }

        var mutableCommandLine = new StringBuilder(commandLine);
        if (!CreateProcessW(
                applicationName,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                IntPtr.Zero,
                null,
                ref startup,
                out var processInformation))
        {
            return null;
        }

        try
        {
            return Process.GetProcessById((int)processInformation.dwProcessId);
        }
        finally
        {
            CloseHandle(processInformation.hProcess);
            CloseHandle(processInformation.hThread);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
