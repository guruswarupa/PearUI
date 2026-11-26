using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MacUI.Core;

/// <summary>
/// Makes Windows Terminal look like macOS Terminal ("Pro" colors, translucent acrylic, dark chrome),
/// with Command Prompt as the default profile, and sets Windows Terminal as the default terminal app.
/// Run by the installer via "PearUI.dll --setup-terminal". The original settings are backed up once.
/// </summary>
public static class TerminalSetup
{
    private const string CmdGuid = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}";

    private static IEnumerable<string> SettingsFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(local, @"Microsoft\Windows Terminal\settings.json"); // unpackaged / portable
    }

    /// <summary>Returns a short report of what was changed.</summary>
    public static string Apply()
    {
        var report = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool packaged = Directory.Exists(Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe"));

        var files = SettingsFiles().Where(File.Exists).ToList();
        // Installed but never opened: create the settings file; Terminal fills in the rest on first start.
        if (files.Count == 0 && packaged)
        {
            string f = SettingsFiles().First();
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, "{}");
            files.Add(f);
        }

        foreach (var file in files)
        {
            try { StyleFile(file); report.Add("styled " + file); }
            catch (Exception ex) { report.Add("could not style " + file + ": " + ex.Message); }
        }

        // Windows Terminal as the default terminal app (Settings > System > For developers > Terminal).
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Console\%%Startup");
            k.SetValue("DelegationConsole", "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}");
            k.SetValue("DelegationTerminal", "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}");
            report.Add("Windows Terminal set as the default terminal");
        }
        catch (Exception ex) { report.Add("could not set default terminal: " + ex.Message); }

        return string.Join(Environment.NewLine, report);
    }

    private static void StyleFile(string file)
    {
        string text = File.ReadAllText(file);
        string backup = file + ".pearui-backup";
        if (!File.Exists(backup)) File.WriteAllText(backup, text);

        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text, null,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? new JsonObject();

        // ----- profiles: Command Prompt is the default, styled like macOS Terminal -----
        JsonArray list;
        if (root["profiles"] is JsonArray arr) list = arr;
        else
        {
            var profiles = root["profiles"] as JsonObject ?? new JsonObject();
            root["profiles"] = profiles;
            list = profiles["list"] as JsonArray ?? new JsonArray();
            profiles["list"] = list;
        }
        var cmd = list.OfType<JsonObject>().FirstOrDefault(p =>
            string.Equals((string)p["guid"], CmdGuid, StringComparison.OrdinalIgnoreCase));
        if (cmd == null)
        {
            cmd = new JsonObject { ["guid"] = CmdGuid, ["name"] = "Command Prompt", ["commandline"] = @"%SystemRoot%\System32\cmd.exe" };
            list.Insert(0, cmd);
        }
        cmd["hidden"] = false;
        cmd["colorScheme"] = "PearUI Pro";
        cmd["opacity"] = 78;
        cmd["useAcrylic"] = true;
        cmd["font"] = new JsonObject { ["face"] = "Cascadia Mono", ["size"] = 13 };
        cmd["padding"] = "10, 8, 10, 8";
        cmd["cursorShape"] = "filledBox";
        cmd["startingDirectory"] = "%USERPROFILE%";
        cmd["tabTitle"] = "Terminal";
        root["defaultProfile"] = CmdGuid;

        // ----- macOS Terminal "Pro" colors -----
        var schemes = root["schemes"] as JsonArray ?? new JsonArray();
        root["schemes"] = schemes;
        RemoveNamed(schemes, "PearUI Pro");
        schemes.Add(new JsonObject
        {
            ["name"] = "PearUI Pro",
            ["background"] = "#1E1E1E", ["foreground"] = "#E6E6E6", ["cursorColor"] = "#B4B4B4", ["selectionBackground"] = "#4A4A50",
            ["black"] = "#000000", ["red"] = "#C23621", ["green"] = "#25BC24", ["yellow"] = "#ADAD27",
            ["blue"] = "#492EE1", ["purple"] = "#D338D3", ["cyan"] = "#33BBC8", ["white"] = "#CBCCCD",
            ["brightBlack"] = "#818383", ["brightRed"] = "#FC391F", ["brightGreen"] = "#31E722", ["brightYellow"] = "#EAEC23",
            ["brightBlue"] = "#5833FF", ["brightPurple"] = "#F935F8", ["brightCyan"] = "#14F0F0", ["brightWhite"] = "#E9EBEB",
        });

        // ----- dark, minimal window chrome -----
        var themes = root["themes"] as JsonArray ?? new JsonArray();
        root["themes"] = themes;
        RemoveNamed(themes, "PearUI");
        themes.Add(new JsonObject
        {
            ["name"] = "PearUI",
            ["window"] = new JsonObject { ["applicationTheme"] = "dark", ["useMica"] = false, ["rainbowFrame"] = false },
            ["tabRow"] = new JsonObject { ["background"] = "#262628FF", ["unfocusedBackground"] = "#2E2E30FF" },
            ["tab"] = new JsonObject { ["background"] = "#262628FF", ["unfocusedBackground"] = "#2E2E30FF", ["showCloseButton"] = "hover" },
        });
        root["theme"] = "PearUI";

        var compat = root["compatibility"] as JsonObject ?? new JsonObject();
        compat["enableUnfocusedAcrylic"] = true; // stay translucent when not focused
        root["compatibility"] = compat;
        root["initialCols"] ??= 100;
        root["initialRows"] ??= 28;

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RemoveNamed(JsonArray arr, string name)
    {
        foreach (var n in arr.OfType<JsonObject>().Where(o => (string)o["name"] == name).ToList()) arr.Remove(n);
    }
}
