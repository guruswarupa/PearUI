using System.Diagnostics;
using System.IO;
using System.Windows;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>Helpers to start apps, open URIs and perform power actions.</summary>
public static class Launch
{
    public static void Open(string target, string args = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open \"{target}\".\n\n{ex.Message}", "PearUI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens PearUI Terminal; if it can't start, falls back to Command Prompt.</summary>
    public static void Terminal(bool newTab = false)
    {
        try { global::PearUI.Terminal.TerminalWindow.ShowOrActivate(newTab); }
        catch { try { Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true }); } catch { } }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    public static void Settings(string page = "") => Open("ms-settings:" + page);

    public static void Sleep() => Win32.SetSuspendState(false, false, false);

    public static void Lock() => Win32.LockWorkStation();

    public static void Restart()
    {
        if (Confirm("Are you sure you want to restart your computer now?", "Restart"))
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }

    public static void ShutDown()
    {
        if (Confirm("Are you sure you want to shut down your computer now?", "Shut Down"))
            Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }

    public static void LogOut()
    {
        if (Confirm($"Are you sure you want to sign out {Environment.UserName}?", "Log Out"))
            Process.Start(new ProcessStartInfo("shutdown", "/l") { CreateNoWindow = true, UseShellExecute = false });
    }

    private static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
