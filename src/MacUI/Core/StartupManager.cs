using System.IO;
using Microsoft.Win32;

namespace MacUI.Core;

/// <summary>
/// "Start with Windows": a shortcut in the user's Startup folder. A shortcut (unlike a Run-key entry)
/// can start minimized.
///
/// The shortcut starts PearUI.exe, so Task Manager → Startup apps shows "PearUI" with its icon and publisher.
/// Only when Smart App Control is enforcing (it blocks unsigned programs) does it fall back to starting
/// PearUI.dll through the signed .NET host, which Windows then lists as ".NET Host".
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PearUI.lnk");

    /// <summary>True when Smart App Control would block an unsigned PearUI.exe.</summary>
    public static bool SmartAppControlBlocksExe
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
                return k?.GetValue("VerifiedAndReputablePolicyState") is int state && state != 0; // 1 = on, 2 = evaluating
            }
            catch { return false; }
        }
    }

    /// <summary>What a shortcut to this copy of PearUI should run: (program, arguments).</summary>
    public static (string Target, string Arguments) LaunchTarget()
    {
        string apphost = Path.Combine(AppContext.BaseDirectory, "PearUI.exe");
        if (File.Exists(apphost) && !SmartAppControlBlocksExe) return (apphost, "");

        string host = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(host) && Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return (host, $"\"{typeof(StartupManager).Assembly.Location}\"");
        return (host, "");
    }

    private const string TaskName = "PearUI";
    private const string SerializeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";

    /// <summary>
    /// Starts PearUI as early as possible at sign-in, with high priority. A sign-in task (Task Scheduler) runs
    /// immediately at logon, unlike Startup-folder items which Explorer delays by several seconds. If the task
    /// can't be created, the Startup-folder shortcut is used instead (and Explorer's delay is removed for it).
    /// </summary>
    public static void Apply(bool enabled)
    {
        // Clean up the Run-key entries used by earlier versions.
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.DeleteValue("MacUI", false);
            k.DeleteValue("PearUI", false);
        }
        catch { }

        if (!enabled)
        {
            DeleteLogonTask();
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { using var k = Registry.CurrentUser.OpenSubKey(SerializeKey, true); k?.DeleteValue("StartupDelayInMSec", false); } catch { }
            return;
        }

        // No Explorer start-up delay for this user's startup items.
        try { using var k = Registry.CurrentUser.CreateSubKey(SerializeKey); k.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord); } catch { }

        if (CreateLogonTask())
        {
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { } // never start twice
            return;
        }
        CreateStartupShortcut();
    }

    private static string XmlEscape(string s) => System.Security.SecurityElement.Escape(s);

    private static bool CreateLogonTask()
    {
        // Priority 2 (above normal) is the highest Windows lets a non-admin task have; 0-1 need administrator rights.
        foreach (int priority in new[] { 2, 4 })
            if (CreateLogonTask(priority)) return true;
        return false;
    }

    private static bool CreateLogonTask(int priority)
    {
        try
        {
            var (exe, args) = LaunchTarget();
            if (string.IsNullOrEmpty(exe)) return false;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Author>Guru Swarupa</Author><Description>Starts PearUI as soon as you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{XmlEscape(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{XmlEscape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>{priority}</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{XmlEscape(exe)}</Command>
      <Arguments>{XmlEscape(args)}</Arguments>
      <WorkingDirectory>{XmlEscape(AppContext.BaseDirectory.TrimEnd('\\'))}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "pearui-logon-task.xml");
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            try { File.Delete(file); } catch { }
            return code == 0;
        }
        catch { return false; }
    }

    private static void DeleteLogonTask()
    {
        try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); } catch { }
    }

    private static int RunSchtasks(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        });
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }

    private static void CreateStartupShortcut()
    {
        try
        {
            var (exe, args) = LaunchTarget();
            if (string.IsNullOrEmpty(exe)) return;
            string workDir = AppContext.BaseDirectory;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = exe;
            lnk.Arguments = args;
            lnk.WorkingDirectory = workDir;
            lnk.WindowStyle = 7; // minimized
            string ico = Path.Combine(workDir, "PearUI.ico");
            if (File.Exists(ico)) lnk.IconLocation = ico;
            lnk.Description = "PearUI";
            lnk.Save();
        }
        catch { }
    }
}
