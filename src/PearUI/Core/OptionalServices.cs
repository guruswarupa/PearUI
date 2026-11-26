using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>
/// A short list of optional Windows services the user can turn off to free memory and CPU.
/// Nothing is changed unless the user ticks a service and presses Apply (that needs administrator
/// rights, so the change is made by an elevated copy of PearUI after a UAC prompt).
/// </summary>
public static class OptionalServices
{
    public sealed record Def(string Name, string Title, string Description, int DefaultStart, bool DefaultDelayed = false);

    public static readonly Def[] All =
    {
        new("SysMain", "SysMain (Superfetch)", "Pre-loads apps into memory. Gives little on an SSD and adds background disk use.", 2),
        new("PhoneSvc", "Phone Service", "Only used by Phone Link and cellular features.", 3),
        new("Spooler", "Print Spooler", "Only needed if you print. Turn it back on before printing.", 2),
    };

    private static readonly HashSet<string> Allowed = All.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the service is currently set to Disabled (readable without administrator rights).</summary>
    public static bool IsDisabled(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            return k?.GetValue("Start") is int start && start == 4;
        }
        catch { return false; }
    }

    public static bool Exists(string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name); return k != null; }
        catch { return false; }
    }

    /// <summary>Launches an elevated copy of PearUI that applies the changes. False if the user declined the UAC prompt.</summary>
    public static bool ApplyElevated(IEnumerable<(string Name, bool Disable)> changes, IEnumerable<(string Id, bool Apply)> tweaks = null)
    {
        string spec = string.Join(",", changes.Select(c => (c.Disable ? "disable:" : "restore:") + c.Name));
        string tspec = string.Join(",", (tweaks ?? Array.Empty<(string, bool)>()).Select(c => (c.Apply ? "a:" : "u:") + c.Id));
        string args = (spec.Length > 0 ? "--services " + spec + " " : "") + (tspec.Length > 0 ? "--debloat " + tspec : "");
        if (args.Length == 0) return true;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
            return true;
        }
        catch (Win32Exception) { return false; } // UAC declined
        catch { return false; }
    }

    /// <summary>Runs inside the elevated copy (--services disable:Name,restore:Name).</summary>
    public static void ApplyFromArgs(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2 || !Allowed.Contains(bits[1])) continue;
            var def = All.First(d => d.Name.Equals(bits[1], StringComparison.OrdinalIgnoreCase));
            if (bits[0] == "disable")
            {
                Sc("config", def.Name, "start=", "disabled");
                Sc("stop", def.Name);
            }
            else if (bits[0] == "restore")
            {
                string start = def.DefaultStart == 2 ? (def.DefaultDelayed ? "delayed-auto" : "auto") : "demand";
                Sc("config", def.Name, "start=", start);
                if (def.DefaultStart == 2) Sc("start", def.Name);
            }
        }
    }

    private static void Sc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe") { CreateNoWindow = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { }
    }
}
