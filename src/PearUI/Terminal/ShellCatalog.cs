using System.Diagnostics;
using System.IO;
using System.Text;
using PearUI.Core;

namespace PearUI.Terminal;

/// <summary>One shell PearUI Terminal can run.</summary>
internal sealed record ShellDef(string Id, string Name, string Kind, string Exe)
{
    /// <summary>True for Linux-style shells (bash, zsh, fish, sh…).</summary>
    public bool IsPosix => Kind is "posix" or "zsh" or "fish" or "sh";
}

/// <summary>
/// Finds the shells installed on this PC (Command Prompt, PowerShell, and Linux shells from Git for Windows, MSYS2,
/// Cygwin or WSL) and works out how to start each one with a Mac/Linux-style prompt.
/// </summary>
internal static class ShellCatalog
{
    private static List<ShellDef> _cache;
    private static readonly object Gate = new();

    // id → (display name, kind)
    private static readonly (string Id, string Name, string Kind)[] Known =
    {
        ("bash", "Bash", "posix"),
        ("zsh", "Zsh", "zsh"),
        ("fish", "Fish", "fish"),
        ("sh", "sh", "sh"),
        ("dash", "Dash", "sh"),
        ("ksh", "Korn shell (ksh)", "sh"),
        ("nu", "Nushell", "plain"),
        ("pwsh", "PowerShell 7", "ps"),
    };

    public static IReadOnlyList<ShellDef> All()
    {
        lock (Gate) return _cache ??= Detect();
    }

    public static void Refresh() { lock (Gate) _cache = null; }

    public static ShellDef Find(string id) => string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(s => s.Id == id);

    /// <summary>The shell new tabs open with: the saved choice, else Bash if it is installed (a Linux-style start), else Command Prompt.</summary>
    public static ShellDef Default() => Find(SettingsStore.Current.TerminalShell) ?? Find("bash") ?? Find("cmd") ?? All()[0];

    public static void SaveDefault(string id)
    {
        if (SettingsStore.Current.TerminalShell == id) return;
        SettingsStore.Current.TerminalShell = id;
        SettingsStore.Save(raiseEvent: false);
    }

    /// <summary>
    /// If what the user typed is just the name of a shell ("zsh", "bash -l", "pwsh.exe"), returns that shell's id,
    /// so PearUI can open with it next time.
    /// </summary>
    public static string IdFromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string word = command.Trim().Split(' ', '\t')[0].Trim('"').Replace('\\', '/');
        word = word[(word.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (word.EndsWith(".exe")) word = word[..^4];
        if (word == "nushell") word = "nu";
        if (word == "powershell" || word == "cmd" || word == "wsl" || Known.Any(k => k.Id == word)) return Find(word)?.Id;
        return null;
    }

    // ================= detection =================
    private static List<ShellDef> Detect()
    {
        var list = new List<ShellDef>();
        foreach (var (id, name, kind) in Known)
        {
            string exe = FindExe(id);
            if (exe != null) list.Add(new ShellDef(id, name, kind, exe));
        }

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string ps = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(ps)) list.Add(new ShellDef("powershell", "Windows PowerShell", "ps", ps));
        list.Add(new ShellDef("cmd", "Command Prompt", "cmd", Path.Combine(sys, "cmd.exe")));
        string wsl = Path.Combine(sys, "wsl.exe");
        if (File.Exists(wsl) && HasWslDistro(wsl)) list.Add(new ShellDef("wsl", "WSL (Linux)", "wsl", wsl));
        return list;
    }

    private static bool HasWslDistro(string wsl)
    {
        // Registered distributions are listed in the registry, so no process needs to be started.
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return k != null && k.GetSubKeyNames().Length > 0;
        }
        catch { return false; }
    }

    private static IEnumerable<string> SearchDirs()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var git in new[] { Path.Combine(pf, "Git"), Path.Combine(pf86, "Git"), Path.Combine(local, "Programs", "Git") })
        {
            yield return Path.Combine(git, "usr", "bin");
            yield return Path.Combine(git, "bin");
        }
        foreach (var root in new[] { @"C:\msys64", @"D:\msys64", @"C:\cygwin64", @"C:\cygwin" })
        {
            yield return Path.Combine(root, "usr", "bin");
            yield return Path.Combine(root, "bin");
            yield return Path.Combine(root, "ucrt64", "bin");
            yield return Path.Combine(root, "mingw64", "bin");
        }
        yield return Path.Combine(pf, "PowerShell", "7");
        yield return Path.Combine(local, "Programs", "nu", "bin");
        yield return Path.Combine(pf, "nu", "bin");
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) yield return p.Trim('"');
    }

    private static string FindExe(string name)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dir in SearchDirs())
        {
            try
            {
                // C:\Windows\System32\bash.exe is only a launcher for WSL, not a shell of its own.
                if (dir.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                string file = Path.Combine(dir, name + ".exe");
                if (File.Exists(file)) return file;
            }
            catch { }
        }
        return null;
    }

    // ================= starting a shell =================
    public static string InitDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PearUI", "shell");

    /// <summary>Shell scripts must use Unix line endings, whatever this source file uses.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n") + "\n";

    private static string Fwd(string p) => p.Replace('\\', '/');

    /// <summary>The command line to run and the environment variables to add, for a Linux-style (or Mac-style) prompt.</summary>
    public static (string CommandLine, Dictionary<string, string> Env) BuildLaunch(ShellDef s)
    {
        string user = Environment.UserName, host = Environment.MachineName;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        string shellDir = Path.GetDirectoryName(s.Exe);

        var env = new Dictionary<string, string> { ["TERM_PROGRAM"] = "PearUI", ["COLORTERM"] = "truecolor" };
        // "info" lives next to PearUI.exe; Linux shells also need their own tools (ls, grep…) on the path.
        env["PATH"] = (s.IsPosix ? shellDir + ";" : "") + appDir + ";" + oldPath;

        if (s.IsPosix || s.Kind == "plain")
        {
            env["TERM"] = "xterm-256color";
            if (Environment.GetEnvironmentVariable("LANG") == null) env["LANG"] = "en_US.UTF-8";
            if (Environment.GetEnvironmentVariable("HOME") == null) env["HOME"] = home;
            env["CHERE_INVOKING"] = "1";
        }

        Directory.CreateDirectory(InitDir);
        string q(string x) => "\"" + x + "\"";

        switch (s.Kind)
        {
            case "cmd":
                env["PROMPT"] = $"$E]0;$P$E\\$E[32m{user.Replace("$", "$$")}@{host.Replace("$", "$$")}$E[0m $E[34m$P$E[0m % ";
                return (q(s.Exe), env);

            case "ps":
            {
                // "user@host folder % ": the " % " lets the history recorder tell the prompt from what was typed.
                const string prompt = "function prompt { $e=[char]27; \"$e]0;$($PWD.Path)$e\\$e[32m$env:USERNAME@$env:COMPUTERNAME$e[0m $e[34m$($PWD.Path)$e[0m % \" }";
                return ($"{q(s.Exe)} -NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(prompt)), env);
            }

            case "posix":   // bash
            {
                string rc = Path.Combine(InitDir, "bashrc");
                File.WriteAllText(rc, Lf(BashRc), new UTF8Encoding(false));
                return ($"{q(s.Exe)} --rcfile {q(Fwd(rc))} -i", env);
            }

            case "sh":      // sh, dash, ksh
            {
                string rc = Path.Combine(InitDir, "shrc");
                File.WriteAllText(rc, Lf(ShRc), new UTF8Encoding(false));
                env["ENV"] = Fwd(rc);
                return ($"{q(s.Exe)} -i", env);
            }

            case "zsh":
            {
                string dir = Path.Combine(InitDir, "zsh");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, ".zshrc"), Lf(ZshRc), new UTF8Encoding(false));
                env["ZDOTDIR"] = Fwd(dir);
                return ($"{q(s.Exe)} -i", env);
            }

            case "fish":
                return ($"{q(s.Exe)} -C {q(FishInit)}", env);

            case "wsl":
                return ($"{q(s.Exe)} --cd ~", env);

            default:        // nushell and anything else: its own prompt
                return (q(s.Exe), env);
        }
    }

    // user@host folder %   (green, blue, then a percent sign), with the folder in the window title.
    private const string BashRc = """
        [ -f /etc/profile ] && . /etc/profile
        [ -f ~/.bashrc ] && . ~/.bashrc
        alias ls='ls --color=auto' 2>/dev/null
        alias grep='grep --color=auto' 2>/dev/null
        alias ll='ls -la' 2>/dev/null
        PS1='\[\e]0;\w\a\]\[\e[1;32m\]\u@\h\[\e[0m\] \[\e[1;34m\]\w\[\e[0m\] % '
        """;

    private const string ShRc = """
        if [ -n "$BASH_VERSION" ]; then
          PS1='\[\e]0;\w\a\]\[\e[1;32m\]\u@\h\[\e[0m\] \[\e[1;34m\]\w\[\e[0m\] % '
        else
          PS1='$PWD % '
        fi
        """;

    private const string ZshRc = """
        [[ -f "$HOME/.zshrc" ]] && source "$HOME/.zshrc"
        precmd() { print -Pn '\e]0;%~\a' }
        PROMPT='%F{green}%B%n@%m%b%f %F{blue}%B%~%b%f %% '
        """;

    private const string FishInit =
        "set -g fish_greeting; " +
        "function fish_prompt; set_color -o green; printf '%s@%s' $USER (prompt_hostname); set_color normal; printf ' '; " +
        "set_color -o blue; printf '%s' (prompt_pwd); set_color normal; printf ' %% '; end; " +
        "function fish_title; prompt_pwd; end";
}
