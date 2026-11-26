using System.Runtime.InteropServices;

namespace MacUI.Native;

/// <summary>Master volume of the default playback device (Core Audio).</summary>
public static class AudioVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

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

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float levelDb);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IAudioEndpointVolume GetEndpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device) != 0 || device == null)
                return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
            return o as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    /// <summary>0..1, or -1 when no output device exists.</summary>
    public static float GetVolume()
    {
        var ep = GetEndpoint();
        if (ep == null) return -1;
        ep.GetMasterVolumeLevelScalar(out float v);
        return v;
    }

    public static void SetVolume(float value)
    {
        var ep = GetEndpoint();
        if (ep == null) return;
        var g = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref g);
        if (value > 0) ep.SetMute(false, ref g);
    }

    public static bool GetMute()
    {
        var ep = GetEndpoint();
        if (ep == null) return false;
        ep.GetMute(out bool m);
        return m;
    }

    public static void SetMute(bool mute)
    {
        var ep = GetEndpoint();
        if (ep == null) return;
        var g = Guid.Empty;
        ep.SetMute(mute, ref g);
    }
}
