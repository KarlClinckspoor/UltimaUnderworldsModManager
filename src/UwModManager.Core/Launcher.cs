using System.Diagnostics;

namespace UwModManager.Core;

/// <summary>Starts the games: DOSBox on a DOS folder, or the Exhumed port on a home.</summary>
public static class Launcher
{
    /// <summary>DOSBox's program: the setting, else GOG's in the game folder, else dosbox on the PATH.</summary>
    public static string? FindDosBox(Settings s, string folder)
    {
        if (!string.IsNullOrEmpty(s.DosBox) && File.Exists(s.DosBox)) return s.DosBox;
        foreach (var rel in new[] { "DOSBOX/DOSBox.exe", "DOSBOX/dosbox.exe", "dosbox/DOSBox.exe" })
        {
            string p = Path.Combine(folder, rel);
            if (File.Exists(p)) return p;
        }
        foreach (var name in new[] { "dosbox-staging", "dosbox", "dosbox-x" })
        {
            string? p = OnPath(name);
            if (p != null) return p;
        }
        return null;
    }

    static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" })
            {
                string p = Path.Combine(dir, name + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>
    /// The DOSBox command for a game folder: GOG's own setups when the folder has them (the base
    /// one first, then the game's, as GOG's shortcuts do), else a setup the manager writes that
    /// mounts the folder and starts the game.
    /// </summary>
    public static ProcessStartInfo DosBox(AppPaths paths, Settings s, Target t)
    {
        string exe = FindDosBox(s, t.Folder) ?? throw new FileNotFoundException("DOSBox was not found: set its program in Settings");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        var confs = Directory.GetFiles(t.Folder, "dosbox*.conf").OrderBy(c => Path.GetFileName(c).Contains('_') ? 1 : 0).ThenBy(c => c).ToList();
        if (confs.Count > 0)
        {
            psi.WorkingDirectory = Directory.Exists(Path.Combine(t.Folder, "DOSBOX")) ? Path.Combine(t.Folder, "DOSBOX") : t.Folder;
            foreach (var c in confs) { psi.ArgumentList.Add("-conf"); psi.ArgumentList.Add(c); }
            psi.ArgumentList.Add("-noconsole");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exit");
            return psi;
        }
        string conf = Path.Combine(paths.DosBox, t.Id + ".conf");
        File.WriteAllText(conf, "# written by Ultima Underworlds Mod Manager for " + t.Name + "\n[cpu]\ncycles=auto\n[autoexec]\nmount c \"" + t.Folder + "\"\nc:\n" + t.ExeName + "\nexit\n");
        psi.WorkingDirectory = t.Folder;
        psi.ArgumentList.Add("-conf");
        psi.ArgumentList.Add(conf);
        return psi;
    }

    public static ProcessStartInfo Exhumed(Target t, string home, IEnumerable<string> enhancements)
    {
        if (string.IsNullOrEmpty(t.PortExe) || !File.Exists(t.PortExe)) throw new FileNotFoundException("the Exhumed program was not found", t.PortExe);
        var psi = new ProcessStartInfo(t.PortExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(t.PortExe)! };
        ExhumedGame.SetConfig(home, t.Game, "enhance", enhancements.Any() ? string.Join(",", enhancements.Distinct()) : null);
        foreach (var a in ExhumedGame.Arguments(t, home, Array.Empty<string>())) psi.ArgumentList.Add(a);
        return psi;
    }

    public static string CommandLine(ProcessStartInfo psi) =>
        "\"" + psi.FileName + "\" " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
}
