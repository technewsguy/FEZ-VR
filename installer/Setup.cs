// FEZ-VR installer and uninstaller: one self-contained exe. The mod files are embedded as resources named
// "payload/<path inside the game folder>" (see build.ps1).
//
// Double-click: finds FEZ in every Steam library (Windows Steam, or Linux Steam when the exe is run through
// Proton/Wine, where the Linux file system is drive Z:), installs, and puts "FEZ-VR Uninstall.exe" (a copy of this
// exe) in the game folder. The mod only adds files: FEZ.exe.config makes .NET load FEZ-VR.exe into FEZ.exe at
// startup, so Steam's normal FEZ entry plays in VR. FEZ's own files are never changed.
//
// Command line (Scoop etc.): /S = silent (log in %TEMP%\fezvr-setup.log), /GAMEDIR=<folder>, /UNINSTALL.
// /VRTAG-WAIT = background helper (started by the mod): lists the game in SteamVR's VR tab after Steam closes.
// Exit code 0 = success.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static partial class FezVrSetup
{
    const string GameName = "FEZ";
    const string SteamAppId = "224760";
    const string GameExe = "FEZ.exe";
    const string DefaultInstallDir = "FEZ";
    const string UninstallerName = "FEZ-VR Uninstall.exe";
    const string ConfigName = "FEZ.exe.config";
    const string ConfigMarker = "FezVr.Boot";
    // Everything the mod puts in the game folder (also v0.1.0's files), removed on uninstall.
    static readonly string[] ModFiles = { "FEZ-VR.exe", "0Harmony.dll", "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll",
        "Mono.Cecil.Rocks.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "openvr_api.dll", "fezvr.log", "fezvr_red.png" };
    static readonly string[] ModDirs = { "fezvr_input", "fezvr_dump" };

    internal static Action<string> Log = s => { };
    static bool Silent;

    // ---------------------------------------------------------------- entry point

    [STAThread]
    static int Main(string[] args)
    {
        string gameDir = null;
        bool uninstall = Path.GetFileName(SelfPath).IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
        foreach (var a in args) if (a.Equals("/STEAMINPUT", StringComparison.OrdinalIgnoreCase)) SteamVRTag.SteamInput = true;
        foreach (var a in args)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) Silent = true;
            else if (a.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            else if (a.Equals("/VRTAG-WAIT", StringComparison.OrdinalIgnoreCase)) return SteamVRTag.WaitAndTag();
            else if (a.StartsWith("/GAMEDIR=", StringComparison.OrdinalIgnoreCase)) gameDir = a.Substring(9).Trim('"');
        }
        if (uninstall && gameDir == null && File.Exists(Path.Combine(Path.GetDirectoryName(SelfPath), GameExe)))
            gameDir = Path.GetDirectoryName(SelfPath);

        if (Silent) return RunSilent(gameDir, uninstall);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(gameDir, uninstall));
        return 0;
    }

    static int RunSilent(string gameDir, bool uninstall)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "fezvr-setup.log");
        var sb = new StringBuilder();
        Log = s => sb.AppendLine(s);
        int code = 0;
        try
        {
            if (gameDir == null)
            {
                var found = FindGames();
                if (found.Count == 0) throw new Exception("Couldn't find " + GameName + ". Pass /GAMEDIR=<game folder>.");
                gameDir = found[0];
            }
            if (uninstall) Uninstall(gameDir); else Install(gameDir);
        }
        catch (Exception e) { Log("ERROR: " + e.Message); code = 1; }
        try { File.WriteAllText(logPath, sb.ToString()); } catch { }
        Console.Write(sb.ToString());
        return code;
    }

    static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

    // ---------------------------------------------------------------- Wine / Proton

    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    static bool? _wine;
    internal static bool IsWine
    {
        get
        {
            if (_wine == null)
            {
                try { var h = GetModuleHandle("ntdll.dll"); _wine = h != IntPtr.Zero && GetProcAddress(h, "wine_get_version") != IntPtr.Zero; }
                catch { _wine = false; }
            }
            return _wine.Value;
        }
    }

    // "/home/me/.steam" -> "Z:\home\me\.steam" (Wine maps the Linux root to Z:). Windows paths pass through.
    static string FromUnix(string p)
    {
        if (string.IsNullOrEmpty(p) || !p.StartsWith("/")) return p;
        return "Z:" + p.Replace('/', '\\');
    }

    // ---------------------------------------------------------------- finding the game

    static List<string> SteamRoots()
    {
        var roots = new List<string>();
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            foreach (var name in new[] { "SteamPath", "InstallPath" })
            {
                try { var v = Registry.GetValue(key, name, null) as string; if (!string.IsNullOrEmpty(v)) roots.Add(v.Replace('/', '\\')); } catch { }
            }
        if (IsWine)
        {
            var homes = new List<string>();
            string home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home) && home.StartsWith("/")) homes.Add(FromUnix(home));
            try { if (Directory.Exists(@"Z:\home")) homes.AddRange(Directory.GetDirectories(@"Z:\home")); } catch { }
            foreach (var h in homes)
                foreach (var rel in new[] { @".local\share\Steam", @".steam\steam", @".steam\root",
                                            @".var\app\com.valvesoftware.Steam\.local\share\Steam", @".var\app\com.valvesoftware.Steam\.steam\steam" })
                    roots.Add(Path.Combine(h, rel));
        }
        var result = new List<string>();
        foreach (var r in roots)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(r, "steamapps"))) continue;
                string full = Path.GetFullPath(r).TrimEnd('\\');
                if (!result.Exists(x => x.Equals(full, StringComparison.OrdinalIgnoreCase))) result.Add(full);
            }
            catch { }
        }
        return result;
    }

    internal static List<string> FindGames()
    {
        var libs = new List<string>();
        foreach (var root in SteamRoots())
        {
            libs.Add(root);
            try
            {
                string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(FromUnix(m.Groups[1].Value.Replace("\\\\", "\\")));
            }
            catch { }
        }
        var games = new List<string>();
        foreach (var lib in libs)
        {
            try
            {
                string installDir = DefaultInstallDir;
                string acf = Path.Combine(lib, @"steamapps\appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(acf))
                {
                    var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) installDir = m.Groups[1].Value;
                }
                string dir = Path.GetFullPath(Path.Combine(lib, @"steamapps\common\" + installDir));
                if (File.Exists(Path.Combine(dir, GameExe)) && !games.Exists(g => g.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                    games.Add(dir);
            }
            catch { }
        }
        return games;
    }

    internal static bool IsInstalled(string gameDir)
    {
        return !string.IsNullOrEmpty(gameDir) && File.Exists(Path.Combine(gameDir, "FEZ-VR.exe"));
    }

    // ---------------------------------------------------------------- install

    internal static void Install(string gameDir)
    {
        CheckGameDir(gameDir);
        Log("Installing FEZ-VR " + ModVersion + " into " + gameDir);

        RemoveOldVersionLeftovers(gameDir, false);

        var asm = Assembly.GetExecutingAssembly();
        int n = 0;
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("payload/")) continue;
            string rel = res.Substring(8).Replace('/', '\\');
            string dest = Path.Combine(gameDir, rel);
            if (rel.Equals(ConfigName, StringComparison.OrdinalIgnoreCase)) BackUpForeignConfig(dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (var s = asm.GetManifestResourceStream(res))
            using (var o = File.Create(dest)) s.CopyTo(o);
            n++;
        }
        Log("Copied " + n + " files (FEZ-VR, Harmony, openvr_api.dll, " + ConfigName + "). FEZ's own files are unchanged.");

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(SelfPath, uninst, true);
            Log("Uninstaller: " + uninst);
        }

        if (!IsWine) ListInSteamVRSection(uninst);

        Log("");
        Log("Done. Start " + GameName + " from Steam as usual: it plays in VR (SteamVR starts by itself).");
        if (IsWine)
        {
            Log("");
            Log("Linux / Steam Deck: two more steps. In Steam, right-click " + GameName + " > Properties:");
            Log("  1. Compatibility: tick 'Force the use of a specific Steam Play compatibility tool' and pick Proton.");
            Log("  2. Launch Options: enter");
            Log("    " + FezVrSetupLinux.LaunchOption);
        }
    }

    // A FEZ.exe.config that isn't ours (FEZ doesn't ship one) is kept as FEZ.exe.config.fezvr-backup.
    static void BackUpForeignConfig(string path)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).IndexOf(ConfigMarker, StringComparison.Ordinal) < 0)
            {
                File.Copy(path, path + ".fezvr-backup", true);
                Log("Kept the existing " + ConfigName + " as " + ConfigName + ".fezvr-backup.");
            }
        }
        catch { }
    }

    // SteamVR's VR tab: see SteamVRTag.cs. Needs Steam closed; if it's running, ask to restart it, or let the
    // background helper (the uninstaller copy with /VRTAG-WAIT) do it after Steam next closes.
    // Also turns on Steam Input for FEZ (SteamVRTag.SteamInput): PlayStation and other non-Xbox pads only work in
    // FEZ through it. Same file-while-Steam-is-closed rule.
    static void ListInSteamVRSection(string helperExe)
    {
        SteamVRTag.SteamInput = true;
        bool? tagged = SteamVRTag.IsTagged();
        bool inputOn = SteamVRTag.SteamInputOn();
        if (tagged == null) Log("Couldn't read Steam's info for the game, so it isn't listed in SteamVR's VR tab.");
        if (tagged != false && inputOn)
        {
            if (tagged == true) Log("Listed in SteamVR's VR tab; Steam Input is on for " + GameName + ".");
            return;
        }
        try
        {
            if (!SteamVRTag.SteamRunning())
            {
                SteamVRTag.Apply(true);
                Log("Listed in SteamVR's VR tab; Steam Input turned on for " + GameName + " (for PlayStation and other controllers).");
                return;
            }
            if (!Silent && MessageBox.Show("To list " + GameName + " in SteamVR's VR tab and turn on Steam Input for it (needed for PlayStation and other controllers), Steam has to close and reopen once.\r\n\r\nRestart Steam now?\r\n" +
                    "(If you choose No, it's done the next time Steam restarts.)", "FEZ-VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                SteamVRTag.ApplyWithSteamRestart(true, Log, Application.DoEvents);
                Log("Listed in SteamVR's VR tab; Steam Input turned on for " + GameName + " (for PlayStation and other controllers).");
                return;
            }
        }
        catch (Exception e) { Log("Couldn't update Steam's settings for the game: " + e.Message); return; }
        SteamVRTag.StartWaiter(helperExe, true);
        Log("After Steam next restarts, " + GameName + " appears in SteamVR's VR tab and Steam Input is on for it (for PlayStation and other controllers).");
    }

    static void UnlistFromSteamVRSection()
    {
        // Stop a waiting helper first: it would list the game again, and it keeps the uninstaller file in use.
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UninstallerName)))
            try { if (p.Id != Process.GetCurrentProcess().Id) p.Kill(); } catch { }
        if (SteamVRTag.IsTagged() != true) return;
        try
        {
            if (!SteamVRTag.SteamRunning()) { if (SteamVRTag.Apply(false)) Log("Removed from SteamVR's VR tab."); return; }
            if (!Silent && MessageBox.Show("To remove " + GameName + " from SteamVR's VR tab, Steam has to close and reopen once.\r\n\r\nRestart Steam now?",
                    "FEZ-VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (SteamVRTag.ApplyWithSteamRestart(false, Log, Application.DoEvents)) Log("Removed from SteamVR's VR tab.");
                return;
            }
            Log("Note: the game stays in SteamVR's VR tab (run the uninstaller with Steam closed to remove it).");
        }
        catch (Exception e) { Log("Couldn't remove the game from SteamVR's VR tab: " + e.Message); }
    }

    static void CheckGameDir(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(Path.Combine(gameDir, GameExe)))
            throw new Exception("That folder doesn't contain " + GameExe + ". Pick the " + GameName + " folder (steamapps\\common\\" + DefaultInstallDir + ").");
        foreach (var name in new[] { "FEZ", "FEZ-VR" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                string path = null;
                try { path = p.MainModule.FileName; } catch { }
                if (path == null || path.StartsWith(Path.GetFullPath(gameDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(GameName + " is running. Close it first.");
            }
    }

    // ---------------------------------------------------------------- uninstall

    internal static void Uninstall(string gameDir)
    {
        CheckGameDir(gameDir);
        Log("Removing FEZ-VR from " + gameDir);
        RemoveOldVersionLeftovers(gameDir, true);
        if (!IsWine) UnlistFromSteamVRSection();

        // The config first: with it but without FEZ-VR.exe, FEZ wouldn't start.
        string cfg = Path.Combine(gameDir, ConfigName);
        try
        {
            if (File.Exists(cfg) && File.ReadAllText(cfg).IndexOf(ConfigMarker, StringComparison.Ordinal) >= 0) File.Delete(cfg);
            if (File.Exists(cfg + ".fezvr-backup") && !File.Exists(cfg)) { File.Move(cfg + ".fezvr-backup", cfg); Log("Restored the previous " + ConfigName + "."); }
        }
        catch (Exception e) { throw new Exception("Couldn't remove " + cfg + ": " + e.Message); }

        foreach (var f in ModFiles) TryDeleteFile(Path.Combine(gameDir, f));
        foreach (var d in ModDirs) TryDeleteDir(Path.Combine(gameDir, d));
        Log("Removed the mod files.");

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            // A running exe can't delete itself: a short-lived cmd does it a moment after we exit.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del \"" + uninst + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
        else TryDeleteFile(uninst);

        Log("");
        Log("Done. " + GameName + " is back to the normal flat game.");
    }

    // ---------------------------------------------------------------- leftovers of v0.1.0 (separate "FEZ VR" entry)

    // v0.1.0 played through a separate "FEZ VR" entry: a launcher folder, desktop/Start menu shortcuts, a SteamVR
    // library entry and a non-Steam game in Steam with artwork. Now Steam's normal entry plays in VR, so that goes.
    // The non-Steam game entry itself stays in Steam (the user removes it there).
    static void RemoveOldVersionLeftovers(string gameDir, bool uninstalling)
    {
        string ld = Path.Combine(gameDir, "FEZVR-launcher");
        if (!Directory.Exists(ld)) return;

        if (!IsWine)
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                TryDeleteFile(Path.Combine(dir, "FEZ VR.lnk"));

        // The Steam entry's artwork (Steam\userdata\<user>\config\grid\<shortcut id>*)
        string gidFile = Path.Combine(ld, "steam_gameid.txt");
        bool hadSteamEntry = false;
        try
        {
            ulong gid;
            if (File.Exists(gidFile) && ulong.TryParse(File.ReadAllText(gidFile).Trim(), out gid))
            {
                hadSteamEntry = true;
                string steam = WindowsSteamPath();
                string id = (gid >> 32).ToString();
                if (steam != null && Directory.Exists(Path.Combine(steam, "userdata")))
                    foreach (var user in Directory.GetDirectories(Path.Combine(steam, "userdata")))
                    {
                        string grid = Path.Combine(user, @"config\grid");
                        if (Directory.Exists(grid)) foreach (var f in Directory.GetFiles(grid, id + "*")) TryDeleteFile(f);
                    }
            }
        }
        catch { }

        // SteamVR library entry. SteamVR keeps its list in Steam\config\appconfig.json and rewrites that file when it
        // exits, so it can only be edited while SteamVR is closed. Otherwise the mod removes the entry itself the next
        // time the game runs in VR (it needs the manifest file for that, so it stays when installing).
        string manifest = Path.Combine(ld, "fezvr.vrmanifest");
        if (File.Exists(manifest) && Process.GetProcessesByName("vrserver").Length > 0)
        {
            foreach (var f in Directory.GetFiles(ld)) if (!f.Equals(manifest, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(f);
            if (uninstalling)
                Log("Note: SteamVR is running, so the old 'FEZ VR' entry stays in SteamVR's library. Close SteamVR and run the uninstaller again to remove it.");
        }
        else
        {
            if (File.Exists(manifest) && RemoveFromSteamVRConfig(manifest)) Log("Removed the old 'FEZ VR' entry from SteamVR's library.");
            TryDeleteDir(ld);
        }

        Log("Removed v0.1.0's 'FEZ VR' launcher and shortcuts.");
        if (hadSteamEntry)
            Log("Your Steam library still has the old 'FEZ VR' entry. Remove it in Steam: right-click it > Manage > Remove non-Steam game.");
    }

    static string WindowsSteamPath()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; return v == null ? null : v.Replace('/', '\\'); }
        catch { return null; }
    }

    static bool RemoveFromSteamVRConfig(string manifest)
    {
        try
        {
            string steam = WindowsSteamPath();
            if (steam == null) return false;
            string cfg = Path.Combine(steam, @"config\appconfig.json");
            if (!File.Exists(cfg)) return false;
            string text = File.ReadAllText(cfg);
            var arr = Regex.Match(text, "(\"manifest_paths\"\\s*:\\s*\\[)(.*?)(\\])", RegexOptions.Singleline);
            if (!arr.Success) return false;
            var keep = new List<string>();
            bool removed = false;
            string want = Path.GetFullPath(manifest);
            foreach (Match e in Regex.Matches(arr.Groups[2].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string p = e.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                bool same;
                try { same = string.Equals(Path.GetFullPath(p), want, StringComparison.OrdinalIgnoreCase); } catch { same = false; }
                if (same) removed = true; else keep.Add(e.Value);
            }
            if (!removed) return false;
            if (!File.Exists(cfg + ".fezvr-backup")) File.Copy(cfg, cfg + ".fezvr-backup");
            string body = keep.Count == 0 ? "" : "\n      " + string.Join(",\n      ", keep.ToArray()) + "\n   ";
            File.WriteAllText(cfg, text.Substring(0, arr.Index) + arr.Groups[1].Value + body + arr.Groups[3].Value + text.Substring(arr.Index + arr.Length));
            return true;
        }
        catch (Exception e) { Log("Couldn't edit SteamVR's app list: " + e.Message); return false; }
    }

    static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
    static void TryDeleteDir(string p) { try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
}

// ---------------------------------------------------------------- window

class SetupForm : Form
{
    readonly ComboBox _dir = new ComboBox();
    readonly TextBox _log = new TextBox();
    readonly Button _install = new Button(), _uninstall = new Button(), _close = new Button(), _browse = new Button(), _copy = new Button();

    public SetupForm(string gameDir, bool uninstallMode)
    {
        Text = "FEZ-VR " + FezVrSetup.ModVersion + (uninstallMode ? " - Uninstall" : " - Setup");
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        var title = new Label { Text = "VR mod for FEZ (Steam)", Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(12, 10) };
        var lbl = new Label { Text = "Game folder:", AutoSize = true, Location = new Point(12, 48) };
        _dir.Location = new Point(12, 68); _dir.Width = 500; _dir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browse.Text = "Browse..."; _browse.Location = new Point(520, 67); _browse.Width = 88; _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.BackColor = SystemColors.Window;
        _log.Location = new Point(12, 104); _log.Size = new Size(596, 244); _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _install.Text = "Install"; _uninstall.Text = "Uninstall"; _close.Text = "Close"; _copy.Text = "Copy launch option";
        foreach (var b in new[] { _install, _uninstall, _close }) { b.Width = 96; b.Height = 30; b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; }
        _close.Location = new Point(512, 358); _uninstall.Location = new Point(408, 358); _install.Location = new Point(304, 358);
        _copy.Width = 150; _copy.Height = 30; _copy.Location = new Point(12, 358); _copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; _copy.Visible = false;
        Controls.AddRange(new Control[] { title, lbl, _dir, _browse, _log, _install, _uninstall, _close, _copy });

        FezVrSetup.Log = s => { _log.AppendText(s + "\r\n"); _log.Update(); };
        _browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { Description = "Pick the FEZ folder (the one with FEZ.exe)" })
                if (d.ShowDialog(this) == DialogResult.OK) { _dir.Text = d.SelectedPath; UpdateButtons(); }
        };
        _dir.TextChanged += (s, e) => UpdateButtons();
        _close.Click += (s, e) => Close();
        _copy.Click += (s, e) => { try { Clipboard.SetText(FezVrSetupLinux.LaunchOption); } catch { } };
        _install.Click += (s, e) => Run(() => FezVrSetup.Install(_dir.Text.Trim()), true);
        _uninstall.Click += (s, e) => Run(() => FezVrSetup.Uninstall(_dir.Text.Trim()), false);

        var found = FezVrSetup.FindGames();
        foreach (var g in found) _dir.Items.Add(g);
        if (!string.IsNullOrEmpty(gameDir)) _dir.Text = gameDir;
        else if (found.Count > 0) _dir.SelectedIndex = 0;

        if (uninstallMode) FezVrSetup.Log("Click Uninstall to remove FEZ-VR from this game folder.");
        else if (found.Count == 0) FezVrSetup.Log("Couldn't find FEZ in your Steam libraries. Click Browse and pick the game folder (the one with FEZ.exe).");
        else
        {
            FezVrSetup.Log("Found FEZ" + (found.Count > 1 ? " (" + found.Count + " copies; pick one above)" : "") + ".");
            FezVrSetup.Log("Click Install. Afterwards, Steam's normal FEZ entry plays in VR.");
        }
        if (FezVrSetup.IsWine) FezVrSetup.Log("(Running under Proton/Wine.)");
        UpdateButtons();
        AcceptButton = uninstallMode ? _uninstall : _install;
    }

    void UpdateButtons()
    {
        string d = _dir.Text.Trim();
        bool hasGame = d.Length > 0 && File.Exists(Path.Combine(d, "FEZ.exe"));
        _install.Enabled = hasGame;
        _install.Text = FezVrSetup.IsInstalled(d) ? "Reinstall" : "Install";
        _uninstall.Enabled = hasGame && FezVrSetup.IsInstalled(d);
    }

    void Run(Action action, bool installing)
    {
        _install.Enabled = _uninstall.Enabled = _browse.Enabled = _dir.Enabled = false;
        _log.Clear();
        Cursor = Cursors.WaitCursor;
        try
        {
            action();
            if (installing && FezVrSetup.IsWine) _copy.Visible = true;
        }
        catch (Exception e) { FezVrSetup.Log(""); FezVrSetup.Log("Failed: " + e.Message); }
        finally { Cursor = Cursors.Default; _browse.Enabled = _dir.Enabled = true; UpdateButtons(); }
    }
}

// Proton on Linux may not honour FEZ.exe.config, so Steam starts FEZ-VR.exe (which hosts FEZ.exe) instead.
static class FezVrSetupLinux { public const string LaunchOption = "bash -c 'exec \"${@/FEZ.exe/FEZ-VR.exe}\"' -- %command%"; }
