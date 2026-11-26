using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>
/// The optional "Windows debloat" tweaks shown in Settings → Performance. The catalogue is the embedded
/// debloat.json. Each tweak can be switched on and off again; nothing runs unless the user ticks it and presses Apply.
/// </summary>
public static class Debloat
{
    public sealed class RegEntry
    {
        [JsonPropertyName("hive")] public string Hive { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("apply")] public JsonElement Apply { get; set; }
        [JsonPropertyName("undo")] public JsonElement Undo { get; set; }
    }

    public sealed class SvcEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("undoStart")] public string UndoStart { get; set; }
    }

    public sealed class Tweak
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("registry")] public List<RegEntry> Registry { get; set; } = new();
        [JsonPropertyName("services")] public List<SvcEntry> Services { get; set; } = new();
        [JsonPropertyName("applyCommand")] public string ApplyCommand { get; set; }
        [JsonPropertyName("undoCommand")] public string UndoCommand { get; set; }

        /// <summary>Anything that needs administrator rights (machine registry, services, commands).</summary>
        public bool NeedsAdmin => Services.Count > 0 || ApplyCommand != null || Registry.Any(r => r.Hive == "HKLM");
    }

    private static List<Tweak> _all;

    public static IReadOnlyList<Tweak> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PearUI.debloat.json");
                using var r = new StreamReader(s!);
                _all = JsonSerializer.Deserialize<List<Tweak>>(r.ReadToEnd()) ?? new();
            }
            catch { _all = new(); }
            return _all;
        }
    }

    // ---------- state ----------
    private static RegistryKey Hive(string h) => h == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    private static bool IsNull(JsonElement e) => e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>True when every registry value and service of the tweak is already in its "applied" state.</summary>
    public static bool IsApplied(Tweak t)
    {
        if (t.Registry.Count == 0 && t.Services.Count == 0) return false;
        try
        {
            foreach (var r in t.Registry)
            {
                using var k = Hive(r.Hive).OpenSubKey(r.Path);
                object cur = k?.GetValue(r.Name);
                if (IsNull(r.Apply)) { if (cur != null) return false; continue; }
                if (cur == null) return false;
                if (r.Apply.ValueKind == JsonValueKind.String && r.Apply.GetString() == "ram_kb") { if (Convert.ToInt64(cur) <= 3670016) return false; continue; }
                if (r.Apply.ValueKind == JsonValueKind.String) { if (!string.Equals(cur.ToString(), r.Apply.GetString(), StringComparison.OrdinalIgnoreCase)) return false; }
                else if (Convert.ToInt64(cur) != r.Apply.GetInt64()) return false;
            }
            foreach (var s in t.Services)
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s.Name);
                if (k == null) continue; // service does not exist on this PC: nothing to disable
                if (!(k.GetValue("Start") is int st && st == 4)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Hide a tweak when nothing it changes exists on this PC (e.g. no such service).</summary>
    public static bool IsRelevant(Tweak t) =>
        t.Registry.Count > 0 || t.ApplyCommand != null ||
        t.Services.Any(s => { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s.Name); return k != null; });

    // ---------- applying ----------
    /// <summary>Applies the parts that do not need administrator rights (current-user registry). Returns true if more remains for the elevated step.</summary>
    public static bool ApplyUserPart(Tweak t, bool apply)
    {
        foreach (var r in t.Registry.Where(r => r.Hive == "HKCU")) WriteReg(r, apply);
        return t.NeedsAdmin;
    }

    /// <summary>Runs inside the elevated copy of PearUI.</summary>
    public static void ApplyAdminPart(Tweak t, bool apply)
    {
        foreach (var r in t.Registry.Where(r => r.Hive == "HKLM")) WriteReg(r, apply);
        foreach (var s in t.Services)
        {
            if (apply) { Sc("config", s.Name, "start=", "disabled"); Sc("stop", s.Name); }
            else
            {
                Sc("config", s.Name, "start=", s.UndoStart ?? "demand");
                if (s.UndoStart is "auto" or "delayed-auto") Sc("start", s.Name);
            }
        }
        string cmd = apply ? t.ApplyCommand : t.UndoCommand;
        if (!string.IsNullOrWhiteSpace(cmd)) RunCmd(cmd);
    }

    /// <summary>Entry point for "--debloat a:id,u:id" in the elevated copy. Only ids from the built-in catalogue are accepted.</summary>
    public static void ApplyFromArgs(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2) continue;
            var t = All.FirstOrDefault(x => x.Id == bits[1]);
            if (t != null) ApplyAdminPart(t, bits[0] == "a");
        }
    }

    private static void WriteReg(RegEntry r, bool apply)
    {
        try
        {
            var v = apply ? r.Apply : r.Undo;
            if (IsNull(v))
            {
                using var del = Hive(r.Hive).OpenSubKey(r.Path, writable: true);
                del?.DeleteValue(r.Name, false);
                return;
            }
            using var k = Hive(r.Hive).CreateSubKey(r.Path);
            switch (r.Kind)
            {
                case "dword":
                    k.SetValue(r.Name, v.ValueKind == JsonValueKind.String ? RamKb() : v.GetInt32(), RegistryValueKind.DWord);
                    break;
                case "qword": k.SetValue(r.Name, v.GetInt64(), RegistryValueKind.QWord); break;
                default: k.SetValue(r.Name, v.GetString() ?? "", RegistryValueKind.String); break;
            }
        }
        catch { }
    }

    private static int RamKb()
    {
        long kb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024;
        return (int)Math.Clamp(kb, 3670017, int.MaxValue);
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

    private static void RunCmd(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
        }
        catch { }
    }
}
