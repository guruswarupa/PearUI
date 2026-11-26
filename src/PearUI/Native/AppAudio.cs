using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PearUI.Native;

/// <summary>One app that currently has an audio session (the Windows volume mixer's rows).</summary>
public sealed class AppAudioSession
{
    internal List<AppAudio.ISimpleAudioVolume> Volumes { get; } = new();
    public string Name { get; init; }
    public string ExePath { get; init; }

    public float Volume
    {
        get { try { Volumes[0].GetMasterVolume(out float v); return v; } catch { return 0; } }
        set { var g = Guid.Empty; foreach (var v in Volumes) try { v.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref g); if (value > 0) v.SetMute(false, ref g); } catch { } }
    }

    public bool Muted
    {
        get { try { Volumes[0].GetMute(out bool m); return m; } catch { return false; } }
        set { var g = Guid.Empty; foreach (var v in Volumes) try { v.SetMute(value, ref g); } catch { } }
    }
}

/// <summary>Per-app volume (Core Audio sessions) for the default playback device.</summary>
public static class AppAudio
{

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, ref Guid ctx);
        int RegisterAudioSessionNotification(IntPtr notify);
        int UnregisterAudioSessionNotification(IntPtr notify);
        // IAudioSessionControl2
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid ctx);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Apps with an audio session, one row per app (several sessions of one program are grouped).</summary>
    public static List<AppAudioSession> GetSessions()
    {
        var result = new List<AppAudioSession>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device == null) return result;
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out object o);
            var mgr = (IAudioSessionManager2)o;
            mgr.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);

            var byKey = new Dictionary<string, AppAudioSession>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessions.GetSession(i, out var ctl);
                    if (ctl is not ISimpleAudioVolume vol) continue;
                    ctl.GetState(out int state);
                    if (state == 2) continue; // expired

                    string name, exe = null;
                    if (ctl.IsSystemSoundsSession() == 0) name = "System Sounds";
                    else
                    {
                        ctl.GetProcessId(out uint pid);
                        (name, exe) = Describe(pid);
                        if (name == null) continue;
                    }

                    if (!byKey.TryGetValue(name, out var s))
                    {
                        s = new AppAudioSession { Name = name, ExePath = exe };
                        byKey[name] = s;
                        result.Add(s);
                    }
                    s.Volumes.Add(vol);
                }
                catch (Exception ex) { Log(ex); }
            }
        }
        catch (Exception ex) { Log(ex); }
        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Log(Exception ex)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(PearUI.Core.SettingsStore.Folder, "audio.log"), $"[{DateTime.Now:O}] {ex}" + Environment.NewLine); } catch { }
    }

    private static (string Name, string Exe) Describe(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string exe = null, name = null;
            try { exe = p.MainModule?.FileName; } catch { }
            if (exe != null)
            {
                try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
            }
            if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
            return (name, exe);
        }
        catch { return (null, null); }
    }
}
