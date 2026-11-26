using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// Windows blocks a normal program's keyboard hook while an administrator window (Task Manager,
/// an admin terminal, Registry Editor…) is in front, so the Windows key would open the real Start menu there.
/// The key-helper is a tiny second copy of PearUI that runs with administrator rights, without any
/// window, and only handles the Windows key while such a window is in front. It is switched on once
/// from Settings (one UAC prompt) and then started silently by PearUI through a Task Scheduler task.
/// </summary>
public static class KeyHelper
{
    public const string TaskName = "PearUI Keys";
    private const string MainMutex = "PearUI.SingleInstance.v1";
    private const string HelperMutex = "PearUI.KeyHelper.v1";
    private const string TapEvent = "PearUI.WinTap.v1";
    private const string SearchEvent = "PearUI.WinSearch.v1";

    // ---------- main process ----------
    private static EventWaitHandle _tap, _search;

    /// <summary>Called by PearUI: listens for the helper's signals and starts it if it is set up.</summary>
    public static void Start(Action onTap, Action onSearch)
    {
        try
        {
            _tap = Create(TapEvent);
            _search = Create(SearchEvent);
            ThreadPool.RegisterWaitForSingleObject(_tap, (_, _) => onTap(), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(_search, (_, _) => onSearch(), null, -1, false);
            StartHelperIfNeeded();
        }
        catch { }
    }

    public static void StartHelperIfNeeded()
    {
        try
        {
            if (!IsInstalled() || Mutex.TryOpenExisting(HelperMutex, out var m) && Dispose(m)) return;
            Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        }
        catch { }
    }

    private static bool Dispose(Mutex m) { m.Dispose(); return true; }

    public static bool IsInstalled() => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Asks Windows for administrator rights (UAC) and installs or removes the helper task. False if declined.</summary>
    public static bool RequestSetup(bool on)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "--keyhelper-setup " + (on ? "on" : "off")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
            if (on) StartHelperIfNeeded();
            return true;
        }
        catch (Win32Exception) { return false; }
        catch { return false; }
    }

    // ---------- elevated setup (--keyhelper-setup on|off) ----------
    public static void SetupElevated(bool on)
    {
        if (!on) { Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F"); return; }
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string exe = Environment.ProcessPath!;
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Author>Guru Swarupa</Author><Description>Lets the PearUI Windows key work over administrator windows such as Task Manager.</Description></RegistrationInfo>
  <Principals><Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(exe)}</Command><Arguments>--keyhelper</Arguments></Exec></Actions>
</Task>";
        string file = Path.Combine(Path.GetTempPath(), "pearui-keys-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        try { File.Delete(file); } catch { }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s);

    // ---------- helper process (--keyhelper) ----------
    private static StartMenuBlocker _blocker;
    private static Mutex _helperMutex;

    /// <summary>Runs in the elevated helper: hook the Windows key only while an administrator window is in front.</summary>
    public static void RunHelper(System.Windows.Application app)
    {
        var msec = new MutexSecurity();
        msec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        _helperMutex = MutexAcl.Create(true, HelperMutex, out bool first, msec);
        if (!first) { app.Shutdown(); return; }

        var tap = Create(TapEvent);
        var search = Create(SearchEvent);
        _blocker = new StartMenuBlocker(
            onTap: () => tap.Set(),
            onSearch: () => search.Set(),
            shouldHandle: ForegroundIsElevated,
            fullScreen: () => FullScreenGuard.IsFullScreenWindow(Win32.GetForegroundWindow()));

        // Quit with PearUI (allow it time to start first).
        var started = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            bool mainAlive = Mutex.TryOpenExisting(MainMutex, out var m);
            m?.Dispose();
            if (!mainAlive && (DateTime.Now - started).TotalSeconds > 30) { timer.Stop(); _blocker?.Dispose(); app.Shutdown(); }
        };
        timer.Start();
    }

    private static bool ForegroundIsElevated()
    {
        try
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            IntPtr h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return true; // cannot even be opened: certainly a protected / elevated window
            try
            {
                if (!OpenProcessToken(h, 0x0008 /*TOKEN_QUERY*/, out IntPtr tok)) return true;
                try
                {
                    int elevated = 0;
                    return GetTokenInformation(tok, 20 /*TokenElevation*/, ref elevated, 4, out _) && elevated != 0;
                }
                finally { Win32.CloseHandle(tok); }
            }
            finally { Win32.CloseHandle(h); }
        }
        catch { return false; }
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, ref int info, int len, out int ret);

    // ---------- helpers ----------
    /// <summary>An event both the normal and the elevated copy may signal (the default ACL would shut one of them out).</summary>
    private static EventWaitHandle Create(string name)
    {
        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
    }

    private static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
