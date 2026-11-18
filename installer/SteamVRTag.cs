// Lists the game in Steam's VR section (the VR tab of the SteamVR dashboard library) by marking its Steam launch option
// as a VR launch, as real VR games have it: appinfo/config/launch/N/type = "vr" in Steam's local copy of the game's
// store info (Steamppcacheppinfo.vdf). steamclient decides "VR game" from the launch option types (vr / othervr /
// openxr), so this survives Steam rewriting the cache. Steam reads that file at startup and rewrites it on exit, so it
// is only edited while Steam is closed. Steam replaces the game's entry whenever Valve updates the game's store info;
// the mod checks at every launch and re-applies it after Steam closes.
//
// appinfo.vdf (v28/v29): u32 magic, u32 universe, [v29: i64 offset of the key string table], then per app:
// u32 appid, u32 size, u32 info state, u32 last updated, u64 access token, 20-byte text SHA-1, u32 change number,
// 20-byte SHA-1 of the binary data, binary KeyValues data. appid 0 ends the list. In v29 every key is a u32 index into
// the string table at the end of the file. Steam checks the binary SHA-1, which is recomputed here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

static class SteamVRTag
{
    const uint AppId = 224760;   // FEZ
    const uint MagicV28 = 0x07564428, MagicV29 = 0x07564429;

    class Kv { public byte Type; public string Key; public byte[] Raw; public List<Kv> Kids; }

    public static string SteamPath()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; return v == null ? null : v.Replace('/', '\\'); }
        catch { return null; }
    }

    public static string AppInfoPath()
    {
        string test = Environment.GetEnvironmentVariable("FEZVR_APPINFO");   // testing against a copy
        if (!string.IsNullOrEmpty(test)) return test;
        string s = SteamPath();
        return s == null ? null : Path.Combine(s, @"appcache\appinfo.vdf");
    }

    public static bool SteamRunning() { return Process.GetProcessesByName("steam").Length > 0; }

    // null = file or game entry not found / unreadable.
    public static bool? IsTagged()
    {
        try
        {
            var launches = new AppInfoFile(File.ReadAllBytes(AppInfoPath())).Launches();
            if (launches == null) return null;
            foreach (var l in launches) if (l.Kids != null && IsWindows(l.Kids) && TypeOf(l.Kids) == "vr") return true;
            return false;
        }
        catch { return null; }
    }

    // Installer only: Apply(true) also turns on Steam Input for FEZ (see SteamInputOn). FEZ's old SDL2 doesn't know
    // PlayStation pads, so without Steam Input presenting them as Xbox pads they do nothing in game. The mod's own
    // re-listing after a Valve update leaves this alone, so a user who turns Steam Input off keeps it off.
    public static bool SteamInput = false;

    // Steam must be closed. Returns true if anything was changed.
    public static bool Apply(bool tag)
    {
        bool input = false;
        if (tag && SteamInput)
        {
            try { input = EnableSteamInput(); } catch { }
        }
        return ApplyLaunchType(tag) | input;
    }

    static bool ApplyLaunchType(bool tag)
    {
        string path = AppInfoPath();
        if (path == null || !File.Exists(path)) return false;
        var f = new AppInfoFile(File.ReadAllBytes(path));
        var launches = f.Launches();
        if (launches == null) return false;
        bool changed = false;
        foreach (var l in launches)
        {
            if (l.Kids == null || !IsWindows(l.Kids)) continue;
            string t = TypeOf(l.Kids);
            if (tag && (t == null || t == "default")) { SetString(l.Kids, "type", "vr"); changed = true; }
            else if (!tag && t == "vr") { SetString(l.Kids, "type", "default"); changed = true; }
        }
        if (!changed) return false;
        byte[] data = f.Save();
        string backup = path + ".fezvr-backup";
        if (!File.Exists(backup)) File.Copy(path, backup);
        string tmp = path + ".fezvr-tmp";
        File.WriteAllBytes(tmp, data);
        File.Copy(tmp, path, true);
        File.Delete(tmp);
        return true;
    }

    // Closes Steam, applies, starts Steam again.
    public static bool ApplyWithSteamRestart(bool tag, Action<string> log, Action pump)
    {
        string steam = SteamPath();
        if (steam == null) return false;
        bool wasRunning = SteamRunning();
        if (wasRunning)
        {
            log("Closing Steam...");
            Process.Start(Path.Combine(steam, "steam.exe"), "-shutdown");
            var sw = Stopwatch.StartNew();
            while (SteamRunning())
            {
                if (sw.Elapsed.TotalSeconds > 90) { log("Steam didn't close."); return false; }
                System.Threading.Thread.Sleep(300);
                if (pump != null) pump();
            }
            System.Threading.Thread.Sleep(1500);
        }
        bool changed = false;
        try { changed = Apply(tag); }
        catch (Exception e) { log("Couldn't update Steam's game info: " + e.Message); }
        if (wasRunning) { log("Starting Steam..."); try { Process.Start(Path.Combine(steam, "steam.exe")); } catch { } }
        return changed;
    }

    // Background helper (started at every game launch, and by the installer): if the game isn't listed, waits until
    // Steam has exited, then lists it. One instance at a time.
    public static int WaitAndTag()
    {
        if (IsTagged() != false && !(SteamInput && !SteamInputOn())) return 0;
        bool created;
        using (var m = new System.Threading.Mutex(true, @"Local\FEZVR-SteamVRTag", out created))
        {
            if (!created) return 0;
            while (SteamRunning()) System.Threading.Thread.Sleep(2000);
            System.Threading.Thread.Sleep(2000);
            try { Apply(true); } catch { return 1; }
            return 0;
        }
    }

    // Started through WMI so it is not a child of the caller: Steam would think the game is still running while it
    // waits, and PowerShell's Start-Process -Wait (Scoop installs) would wait for it too. Note: it doesn't inherit
    // the caller's environment.
    public static void StartWaiter(string exe, bool steamInput = false)
    {
        try
        {
            string cmd = "\"" + exe + "\" /VRTAG-WAIT" + (steamInput ? " /STEAMINPUT" : "");
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"([wmiclass]'Win32_Process').Create('" + cmd.Replace("'", "''").Replace("\"", "\\\"") + "') | Out-Null\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (var p = Process.Start(psi)) p.WaitForExit(15000);
        }
        catch { }
    }

    // ------------------------------------------------------------ Steam Input (per-user localconfig.vdf)

    // The game's Properties > Controller > "Enable Steam Input" is "UseSteamControllerConfig" "2" under
    // UserLocalConfigStore/apps/<appid> in Steam\userdata\<user>\config\localconfig.vdf (text KeyValues).
    // Like appinfo.vdf, Steam rewrites that file on exit, so it is only edited while Steam is closed.
    static List<string> LocalConfigs()
    {
        var list = new List<string>();
        string test = Environment.GetEnvironmentVariable("FEZVR_LOCALCONFIG");   // testing against a copy
        if (!string.IsNullOrEmpty(test)) { list.Add(test); return list; }
        string s = SteamPath();
        if (s == null || !Directory.Exists(Path.Combine(s, "userdata"))) return list;
        foreach (var u in Directory.GetDirectories(Path.Combine(s, "userdata")))
        {
            string f = Path.Combine(u, @"config\localconfig.vdf");
            if (File.Exists(f)) list.Add(f);
        }
        return list;
    }

    // True when every Steam user on this PC has Steam Input set to on for the game (or there's nothing to check).
    public static bool SteamInputOn()
    {
        try
        {
            foreach (var f in LocalConfigs())
                if (SetSteamInput(File.ReadAllText(f, Encoding.UTF8)) != null) return false;
        }
        catch { }
        return true;
    }

    static bool EnableSteamInput()
    {
        bool changed = false;
        foreach (var f in LocalConfigs())
        {
            string text = File.ReadAllText(f, Encoding.UTF8);
            string updated = SetSteamInput(text);
            if (updated == null) continue;
            if (!File.Exists(f + ".fezvr-backup")) File.Copy(f, f + ".fezvr-backup");
            File.WriteAllText(f + ".fezvr-tmp", updated, new UTF8Encoding(false));
            File.Copy(f + ".fezvr-tmp", f, true);
            File.Delete(f + ".fezvr-tmp");
            changed = true;
        }
        return changed;
    }

    class VdfNode { public string Key; public int ValStart, ValEnd; public bool Block; public int Close; public List<VdfNode> Kids; }

    static VdfNode FindNode(List<VdfNode> list, string key)
    {
        foreach (var n in list) if (string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    // The new file text with the setting on, or null if it's already on (or the file isn't a Steam localconfig).
    static string SetSteamInput(string t)
    {
        int i = 0;
        var top = ParseVdf(t, ref i, false);
        var root = FindNode(top, "UserLocalConfigStore");
        if (root == null || !root.Block) return null;
        string app = AppId.ToString();
        var apps = FindNode(root.Kids, "apps");
        if (apps == null || !apps.Block)
            return t.Insert(LineStart(t, root.Close), "\t\"apps\"\n\t{\n\t\t\"" + app + "\"\n\t\t{\n\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\n\t\t}\n\t}\n");
        var a = FindNode(apps.Kids, app);
        if (a == null || !a.Block)
            return t.Insert(LineStart(t, apps.Close), "\t\t\"" + app + "\"\n\t\t{\n\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\n\t\t}\n");
        var k = FindNode(a.Kids, "UseSteamControllerConfig");
        if (k == null) return t.Insert(LineStart(t, a.Close), "\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\n");
        if (k.Block) return null;
        if (t.Substring(k.ValStart, k.ValEnd - k.ValStart) == "2") return null;
        return t.Substring(0, k.ValStart) + "2" + t.Substring(k.ValEnd);
    }

    // Start of the line holding position p, when only whitespace precedes p on it (else p itself).
    static int LineStart(string t, int p)
    {
        int s = p;
        while (s > 0 && (t[s - 1] == '\t' || t[s - 1] == ' ')) s--;
        return s > 0 && t[s - 1] == '\n' ? s : p;
    }

    // Minimal text KeyValues reader that keeps positions: ValStart/ValEnd = a string value's characters (inside the
    // quotes), Close = position of a block's closing brace.
    static List<VdfNode> ParseVdf(string t, ref int i, bool inBlock)
    {
        var list = new List<VdfNode>();
        while (true)
        {
            string key; int ks, ke;
            if (!NextToken(t, ref i, out key, out ks, out ke)) { if (inBlock) throw new InvalidDataException("unclosed block"); return list; }
            if (key == "}") { if (!inBlock) throw new InvalidDataException("unexpected }"); i--; return list; }
            if (key == "{") throw new InvalidDataException("unexpected {");
            var n = new VdfNode { Key = key };
            string v; int vs, ve;
            if (!NextToken(t, ref i, out v, out vs, out ve)) throw new InvalidDataException("missing value");
            if (v == "{" && vs == ve)
            {
                n.Block = true;
                n.Kids = ParseVdf(t, ref i, true);
                n.Close = i;
                i++;
            }
            else { n.ValStart = vs; n.ValEnd = ve; }
            list.Add(n);
        }
    }

    // A quoted string (s/e = its contents) or a brace (s == e). Skips whitespace and // comments.
    static bool NextToken(string t, ref int i, out string tok, out int s, out int e)
    {
        tok = null; s = e = 0;
        while (i < t.Length)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            break;
        }
        if (i >= t.Length) return false;
        if (t[i] == '{' || t[i] == '}') { tok = t[i].ToString(); s = e = i; i++; return true; }
        if (t[i] != '"') throw new InvalidDataException("unexpected character at " + i);
        s = ++i;
        var sb = new StringBuilder();
        while (i < t.Length && t[i] != '"')
        {
            if (t[i] == '\\' && i + 1 < t.Length) { sb.Append(t[i + 1]); i += 2; }
            else sb.Append(t[i++]);
        }
        if (i >= t.Length) throw new InvalidDataException("unclosed string");
        e = i++;
        tok = sb.ToString();
        return true;
    }

    // ------------------------------------------------------------ KeyValues helpers

    static Kv Find(List<Kv> list, string key)
    {
        foreach (var k in list) if (k.Key == key) return k;
        return null;
    }

    // Launch options for Windows (or for every OS). The Mac / Linux ones are left alone.
    static bool IsWindows(List<Kv> launch)
    {
        var cfg = Find(launch, "config");
        var os = cfg != null && cfg.Kids != null ? Find(cfg.Kids, "oslist") : null;
        if (os == null || os.Type != 1 || os.Raw == null) return true;
        string v = Encoding.UTF8.GetString(os.Raw, 0, os.Raw.Length - 1);
        return v.Length == 0 || v.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string TypeOf(List<Kv> launch)
    {
        var e = Find(launch, "type");
        if (e == null || e.Type != 1 || e.Raw == null) return null;
        return Encoding.UTF8.GetString(e.Raw, 0, e.Raw.Length - 1);
    }

    static void SetString(List<Kv> list, string key, string v)
    {
        var e = Find(list, key);
        if (e == null) { e = new Kv { Key = key }; list.Add(e); }
        var b = Encoding.UTF8.GetBytes(v);
        e.Type = 1; e.Raw = new byte[b.Length + 1]; Buffer.BlockCopy(b, 0, e.Raw, 0, b.Length); e.Kids = null;
    }

    class AppInfoFile
    {
        readonly byte[] d;
        readonly uint magic;
        readonly long tableOffset;
        readonly List<string> strings = new List<string>();
        readonly Dictionary<string, int> index = new Dictionary<string, int>();
        readonly int entryPos, entrySize;
        readonly List<Kv> root;

        public AppInfoFile(byte[] data)
        {
            d = data;
            magic = BitConverter.ToUInt32(d, 0);
            if (magic != MagicV28 && magic != MagicV29) throw new InvalidDataException("unknown appinfo.vdf version " + magic.ToString("x8"));
            int pos = 8;
            if (magic == MagicV29)
            {
                tableOffset = BitConverter.ToInt64(d, 8);
                pos = 16;
                int count = BitConverter.ToInt32(d, (int)tableOffset), p = (int)tableOffset + 4;
                for (int i = 0; i < count; i++)
                {
                    int e = Array.IndexOf(d, (byte)0, p);
                    string s = Encoding.UTF8.GetString(d, p, e - p);
                    strings.Add(s);
                    if (!index.ContainsKey(s)) index[s] = i;
                    p = e + 1;
                }
            }
            entryPos = -1;
            while (true)
            {
                uint appid = BitConverter.ToUInt32(d, pos);
                if (appid == 0) break;
                int size = BitConverter.ToInt32(d, pos + 4);
                if (appid == AppId) { entryPos = pos; entrySize = size; break; }
                pos += 8 + size;
            }
            if (entryPos >= 0)
            {
                int p = entryPos + 8 + 60;
                root = ReadMap(ref p);
                if (p != entryPos + 8 + entrySize) throw new InvalidDataException("appinfo entry size mismatch");
            }
        }

        // appinfo/config/launch entries, or null.
        public List<Kv> Launches()
        {
            if (root == null) return null;
            var app = Find(root, "appinfo");
            var list = app != null && app.Kids != null ? app.Kids : root;
            var config = Find(list, "config");
            if (config == null || config.Kids == null) return null;
            var launch = Find(config.Kids, "launch");
            return launch == null ? null : launch.Kids;
        }

        string ReadKey(ref int p)
        {
            if (magic == MagicV29) { int i = BitConverter.ToInt32(d, p); p += 4; return strings[i]; }
            int e = Array.IndexOf(d, (byte)0, p);
            string s = Encoding.UTF8.GetString(d, p, e - p);
            p = e + 1;
            return s;
        }

        List<Kv> ReadMap(ref int p)
        {
            var list = new List<Kv>();
            while (true)
            {
                byte t = d[p++];
                if (t == 8) return list;
                var n = new Kv { Type = t, Key = ReadKey(ref p) };
                switch (t)
                {
                    case 0: n.Kids = ReadMap(ref p); break;
                    case 1: { int e = Array.IndexOf(d, (byte)0, p); n.Raw = Sub(p, e + 1 - p); p = e + 1; break; }
                    case 2: case 3: case 4: case 6: n.Raw = Sub(p, 4); p += 4; break;   // int32, float, pointer, color
                    case 7: case 10: n.Raw = Sub(p, 8); p += 8; break;                 // uint64, int64
                    default: throw new InvalidDataException("unknown KeyValues type " + t);
                }
                list.Add(n);
            }
        }

        byte[] Sub(int p, int n) { var b = new byte[n]; Buffer.BlockCopy(d, p, b, 0, n); return b; }

        void WriteMap(BinaryWriter w, List<Kv> list, List<string> added)
        {
            foreach (var n in list)
            {
                w.Write(n.Type);
                if (magic == MagicV29)
                {
                    int i;
                    if (!index.TryGetValue(n.Key, out i)) { i = strings.Count + added.Count; index[n.Key] = i; added.Add(n.Key); }
                    w.Write(i);
                }
                else { w.Write(Encoding.UTF8.GetBytes(n.Key)); w.Write((byte)0); }
                if (n.Type == 0) WriteMap(w, n.Kids, added); else w.Write(n.Raw);
            }
            w.Write((byte)8);
        }

        public byte[] Save()
        {
            var added = new List<string>();
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) WriteMap(w, root, added);
            byte[] blob = ms.ToArray();
            byte[] header = Sub(entryPos + 8, 60);
            using (var sha = SHA1.Create()) Buffer.BlockCopy(sha.ComputeHash(blob), 0, header, 40, 20);

            var o = new MemoryStream();
            int oldEnd = entryPos + 8 + entrySize;
            int restEnd = magic == MagicV29 ? (int)tableOffset : d.Length;
            o.Write(d, 0, entryPos + 4);
            o.Write(BitConverter.GetBytes(header.Length + blob.Length), 0, 4);
            o.Write(header, 0, header.Length);
            o.Write(blob, 0, blob.Length);
            o.Write(d, oldEnd, restEnd - oldEnd);
            if (magic == MagicV29)
            {
                long newTable = o.Position;
                o.Write(BitConverter.GetBytes(strings.Count + added.Count), 0, 4);
                o.Write(d, (int)tableOffset + 4, d.Length - (int)tableOffset - 4);
                foreach (var s in added) { var b = Encoding.UTF8.GetBytes(s); o.Write(b, 0, b.Length); o.WriteByte(0); }
                byte[] r = o.ToArray();
                Buffer.BlockCopy(BitConverter.GetBytes(newTable), 0, r, 8, 8);
                return r;
            }
            return o.ToArray();
        }
    }
}
