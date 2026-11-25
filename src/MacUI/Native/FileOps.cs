using System.Runtime.InteropServices;

namespace MacUI.Native;

/// <summary>
/// Shell file operations with the normal Windows behavior: progress dialog for big jobs,
/// conflict prompts, and undo (Ctrl+Z in File Explorer). Runs on its own STA thread so the
/// Dock stays responsive.
/// </summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_MOVE = 0x1, FO_DELETE = 0x3;
    private const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40;

    /// <summary>Moves files/folders to the Recycle Bin (no "are you sure" prompt, like macOS; still restorable).</summary>
    public static Task RecycleAsync(IEnumerable<string> paths) =>
        Run(new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = Multi(paths), fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION });

    /// <summary>Moves files/folders into a folder.</summary>
    public static Task MoveAsync(IEnumerable<string> paths, string folder) =>
        Run(new SHFILEOPSTRUCT { wFunc = FO_MOVE, pFrom = Multi(paths), pTo = folder + "\0\0", fFlags = FOF_ALLOWUNDO });

    // The API wants a double-null-terminated list.
    private static string Multi(IEnumerable<string> paths) => string.Join("\0", paths) + "\0\0";

    private static Task Run(SHFILEOPSTRUCT op)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { SHFileOperation(ref op); } catch { }
            tcs.TrySetResult();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
