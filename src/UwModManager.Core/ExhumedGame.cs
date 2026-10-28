using System.Diagnostics;
using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UltimateEditor.Modding.Exe;

namespace UwModManager.Core;

/// <summary>
/// Underworld Exhumed (uw1port, uw2port). The port reads the GOG folder and lays its home folder
/// over it, and never writes the game folder, so a mod goes into a home folder of its own (with
/// its own saved games): its files at their DOS paths, files it removes as .wh. markers, the
/// start square and executable patches in the home's settings file (the fork's --start-square
/// and --exe-patch; docs/MODDING.md there). Each mod, and the original game, has its home under
/// the manager's folder; the player's own home is never written.
/// </summary>
public static class ExhumedGame
{
    public const string PatchFile = "MODS-EXE.PATCH";

    /// <summary>Asks the port what it supports (--features). Null for a build without it.</summary>
    public static PortFeatures? Probe(string portExe)
    {
        try
        {
            var psi = new ProcessStartInfo(portExe, "--features") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } return null; }
            return p.ExitCode == 0 ? ParseFeatures(output) : null;
        }
        catch (Exception) { return null; }
    }

    public static PortFeatures? ParseFeatures(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("features ")) return null;
        var f = new PortFeatures();
        foreach (var l in lines.Skip(1))
        {
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "start-square": f.StartSquare = true; break;
                case "data-overlay": f.DataOverlay = true; break;
                case "exe-patch":
                    foreach (var r in parts.Skip(1))
                    {
                        var ab = r.Split('-');
                        if (ab.Length == 2) f.ExePatchRanges.Add(new[] { Convert.ToInt64(ab[0], 16), Convert.ToInt64(ab[1], 16) });
                    }
                    break;
                case "enhance": if (parts.Length > 1) f.Enhancements = parts[1].Split(',').ToList(); break;
            }
        }
        return f;
    }

    /// <summary>The player's own home folder of the port (where it keeps its settings and saves).</summary>
    public static string NormalHome(string game)
    {
        string name = game == "uw2" ? "uw2port" : "uw1port";
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + name);
    }

    public static string ConfigName(string game) => game == "uw2" ? "uw2port.cfg" : "uw1port.cfg";

    public static string HomeFor(AppPaths paths, Target t, string? mod)
    {
        string name = mod == null ? "original" : mod;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return Path.Combine(paths.ExhumedHomes, t.Id, name.Replace('@', '-'));
    }

    /// <summary>Why the mod cannot be played by this build, or empty.</summary>
    public static List<string> Unsupported(ModManifest m, PortFeatures? f, Func<string, byte[]?> original)
    {
        var r = new List<string>();
        foreach (var c in m.Components)
        {
            if (c is ExeParamComponent p)
            {
                if (p.Param == ExeParams.StartSquare && (f == null || !f.StartSquare))
                    r.Add("the start square needs an Exhumed build with mods (start-square in --features: the fork's modding branch)");
                else if (p.Param != ExeParams.StartSquare) r.Add("the executable setting " + p.Param + " has no Exhumed equivalent");
            }
            else if (c is ExeBytesComponent b)
            {
                if (f == null || !f.ExePatch) { r.Add("byte patches of the executable need an Exhumed build with exe-patch (the fork's modding branch)"); continue; }
                byte[]? exe = original(c.File ?? "");
                foreach (var patch in b.Patches)
                {
                    long at = PatchOffset(exe, patch);
                    long end = at + patch.Base.Length / 2;
                    if (at < 0 || !f.ExePatchRanges.Any(rg => at >= rg[0] && end <= rg[1]))
                        r.Add(string.Format("the patch at {0} changes the executable's code, which Exhumed does not run (only its data can be patched)", new MzExe.Location { Segment = patch.Segment, Offset = patch.Offset }));
                }
            }
        }
        return r;
    }

    /// <summary>Where a patch is in the GOG executable (which is what Exhumed reads).</summary>
    static long PatchOffset(byte[]? exe, ExeBytesComponent.Patch p)
    {
        if (exe == null) return p.FileOffset;
        try { return new MzExe(exe).FileOffset(new MzExe.Location { Segment = p.Segment, Offset = p.Offset }); }
        catch (Exception) { return p.FileOffset; }
    }

    /// <summary>
    /// Makes the home folder of <paramref name="mod"/> (null: the original game) ready: seeded once
    /// from the player's own home (settings, sound cards), the mod's files written, files of an
    /// earlier preparation that are no longer wanted removed, and the settings file's mod keys set.
    /// <paramref name="shield"/>: files of the game folder a DOS mod changed in place, which this
    /// home must hide with the originals (from <paramref name="original"/>).
    /// </summary>
    public static string Prepare(AppPaths paths, Target t, ModPackage? mod, Func<string, byte[]?> original, IEnumerable<string> shield)
    {
        string home = HomeFor(paths, t, mod == null ? null : Library.KeyOf(mod.Manifest));
        bool fresh = !Directory.Exists(home);
        Directory.CreateDirectory(home);
        if (fresh) Seed(home, t.Game);
        var desired = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        string? startSquare = null;
        var patchLines = new List<string>();
        if (mod != null)
        {
            if (mod.Manifest.Game != t.Game) throw new ModApplyException("the mod is for " + mod.Manifest.GameName);
            var why = Unsupported(mod.Manifest, t.Features, original);
            if (why.Count > 0) throw new ModApplyException("this Exhumed build cannot play the mod: " + string.Join("; ", why));
            foreach (var kv in ModApply.Files(mod, original)) desired[kv.Key] = kv.Value;
            byte[]? exe = original(t.ExeName);
            foreach (var c in mod.Manifest.Components)
            {
                if (c is ExeParamComponent p && p.Param == ExeParams.StartSquare) startSquare = p.GetInt("x") + "," + p.GetInt("y");
                if (c is ExeBytesComponent b)
                    foreach (var patch in b.Patches)
                        patchLines.Add(string.Format("0x{0:X} {1} {2}", PatchOffset(exe, patch), patch.Base, patch.Data));
            }
        }
        foreach (var rel in shield)
            if (!desired.ContainsKey(rel)) desired[rel] = original(rel);
        // files written for an earlier preparation of this home that are not wanted now
        if (t.WrittenHome == home)
            foreach (var kv in t.Written)
                if (!desired.ContainsKey(kv.Key))
                {
                    string p = HomePath(home, kv.Key);
                    if (File.Exists(p) && Files.Sha(File.ReadAllBytes(p)) == kv.Value) File.Delete(p);
                    string wh = Whiteout(home, kv.Key);
                    if (kv.Value == UltimateEditor.Modding.Hashing.Absent && File.Exists(wh)) File.Delete(wh);
                }
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in desired)
        {
            string p = HomePath(home, kv.Key);
            if (kv.Value == null)
            {
                // a removed file: the port's whiteout marker hides the game folder's
                if (File.Exists(p)) File.Delete(p);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(Whiteout(home, kv.Key), Array.Empty<byte>());
                written[kv.Key] = UltimateEditor.Modding.Hashing.Absent;
            }
            else
            {
                Files.WriteAtomic(p, kv.Value);
                written[kv.Key] = Files.Sha(kv.Value);
            }
        }
        if (patchLines.Count > 0)
            File.WriteAllText(Path.Combine(home, PatchFile), "# " + (mod == null ? "" : mod.Manifest.Name + " " + mod.Manifest.Version) + ": byte patches of " + t.ExeName + " (OFFSET ORIGINAL NEW)\n" + string.Join("\n", patchLines) + "\n");
        else if (File.Exists(Path.Combine(home, PatchFile))) File.Delete(Path.Combine(home, PatchFile));
        SetConfig(home, t.Game, "start-square", startSquare);
        SetConfig(home, t.Game, "exe-patch", patchLines.Count > 0 ? PatchFile : null);
        t.Written = written;
        t.WrittenHome = home;
        t.ActiveMod = mod == null ? null : Library.KeyOf(mod.Manifest);
        return home;
    }

    static string HomePath(string home, string rel) => Path.Combine(new[] { home }.Concat(GameFolder.Normalize(rel).Split('/')).ToArray());
    static string Whiteout(string home, string rel)
    {
        string p = HomePath(home, rel);
        return Path.Combine(Path.GetDirectoryName(p)!, ".wh." + Path.GetFileName(p));
    }

    /// <summary>A new home starts with the player's settings and sound cards (not their saves or recordings).</summary>
    static void Seed(string home, string game)
    {
        string normal = NormalHome(game);
        string cfg = Path.Combine(normal, ConfigName(game));
        if (File.Exists(cfg))
        {
            var keep = File.ReadAllLines(cfg).Where(l => !l.StartsWith("start-square=") && !l.StartsWith("exe-patch=")).ToList();
            File.WriteAllLines(Path.Combine(home, ConfigName(game)), keep);
        }
        var uwcfg = new GameFolder(normal).Resolve("DATA/UW.CFG");
        if (uwcfg != null)
        {
            Directory.CreateDirectory(Path.Combine(home, "DATA"));
            File.Copy(uwcfg, Path.Combine(home, "DATA", "UW.CFG"), true);
        }
    }

    /// <summary>Sets (or removes, for null) a key of the home's settings file, as the port writes it.</summary>
    public static void SetConfig(string home, string game, string key, string? value)
    {
        string path = Path.Combine(home, ConfigName(game));
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string> { "# " + (game == "uw2" ? "uw2port" : "uw1port") + " settings" };
        lines.RemoveAll(l => l.StartsWith(key + "="));
        if (value != null) lines.Add(key + "=" + value);
        File.WriteAllLines(path, lines);
    }

    /// <summary>The port's arguments for a home: the game folder, the home, and the enhancements.</summary>
    public static List<string> Arguments(Target t, string home, IEnumerable<string> enhancements)
    {
        var a = new List<string> { "--data", t.Folder, "--home", home };
        var e = enhancements.Distinct().ToList();
        if (t.Features != null) e = e.Where(x => t.Features.Enhancements.Count == 0 || t.Features.Enhancements.Contains(x)).ToList();
        if (e.Count > 0) { a.Add("--enhance"); a.Add(string.Join(",", e)); }
        return a;
    }
}
