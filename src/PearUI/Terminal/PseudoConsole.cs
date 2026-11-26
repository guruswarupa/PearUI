using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PearUI.Terminal;

/// <summary>
/// One shell running inside a Windows pseudo console (ConPTY): the shell believes it has a real terminal,
/// and everything it prints (colours, cursor moves, full-screen programs) arrives here as a stream of
/// terminal escape sequences for the front end to draw.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private IntPtr _hPC, _hProcess;
    private SafeFileHandle _inWrite, _outRead;
    private FileStream _input;
    private bool _disposed;

    public event Action<string> Output;
    public event Action Exited;

    public PseudoConsole(string commandLine, string workingDir, int cols, int rows, IDictionary<string, string> extraEnv)
    {
        if (!CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0) || !CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out _hPC);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        inRead.Dispose();   // the pseudo console owns its own copies now
        outWrite.Dispose();

        var si = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() } };
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr env = BuildEnvironment(extraEnv);
        try
        {
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    env, workingDir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _hProcess = pi.hProcess;
            CloseHandle(pi.hThread);
        }
        finally
        {
            Marshal.FreeHGlobal(env);
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        _input = new FileStream(_inWrite, FileAccess.Write, 4096, false);
        new Thread(ReadLoop) { IsBackground = true, Name = "PearUI.Terminal.Read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "PearUI.Terminal.Wait" }.Start();
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buf = new byte[8192];
        var chars = new char[8192];
        try
        {
            using var output = new FileStream(_outRead, FileAccess.Read, 8192, false);
            int n;
            while ((n = output.Read(buf, 0, buf.Length)) > 0)
            {
                int c = decoder.GetChars(buf, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch { }
    }

    private void WaitLoop()
    {
        try { WaitForSingleObject(_hProcess, 0xFFFFFFFF); } catch { }
        if (!_disposed) Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_disposed || _input == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == IntPtr.Zero || cols < 1 || rows < 1) return;
        ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 0); } catch { }
        var hpc = _hPC;
        var proc = _hProcess;
        // ClosePseudoConsole can wait for the output pipe to drain, so never do it on the caller's thread.
        Task.Run(() =>
        {
            try { _input?.Dispose(); } catch { }
            try { if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc); } catch { }
            try { if (proc != IntPtr.Zero) CloseHandle(proc); } catch { }
        });
    }

    private static IntPtr BuildEnvironment(IDictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
        if (extra != null) foreach (var kv in extra) vars[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // ---------- native ----------
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
