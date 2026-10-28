using System.Diagnostics;
using System.Text.Json;
using UltimateEditor.Games;
using UltimateEditor.Modding;

namespace UwModManager.Core;

/// <summary>
/// The manager's operations, for the program and the command line: the targets (DOS folders and
/// Exhumed builds), the library, using a mod (one at a time per target), saved games, capturing
/// changes as a mod, and starting the game. Every change of a DOS folder goes through a journal,
/// so an interrupted change is finished at the next start.
/// </summary>
public sealed class Manager
{
    public AppPaths Paths { get; }
    public ManagerState State { get; }
    public Vault Vault { get; }
    public Library Library { get; }

    public Manager(string? root = null)
    {
        Paths = new AppPaths(root);
        State = ManagerState.Load(Paths.StateFile);
        Vault = new Vault(Paths.Vault);
        Library = new Library(Paths.Library);
    }

    public void Save() => State.Save(Paths.StateFile);

    // ---- targets

    /// <summary>Takes over a DOS game folder: its files' originals into the vault.</summary>
    /// <remarks>A folder the manager took over before says so (DosGame.MarkerName): its
    /// originals and the mod in place come from there, provided this manager's vault has them;
    /// <paramref name="asItIs"/> takes the folder's files as the originals regardless.</remarks>
    public Target AddDos(string folder, string? name = null, bool asItIs = false)
    {
        folder = Path.GetFullPath(folder);
        var game = DosGame.Detect(folder) ?? throw new InvalidOperationException(folder + " holds neither UW.EXE nor UW2.EXE with a DATA folder");
        if (State.Targets.Any(t => t.Kind == TargetKind.Dos && SamePath(t.Folder, folder)))
            throw new InvalidOperationException("the manager already has " + folder);
        var t = new Target { Kind = TargetKind.Dos, Folder = folder, Game = game == GameKind.UW2 ? "uw2" : "uw1" };
        t.Name = name ?? (game == GameKind.UW2 ? "Ultima Underworld II (DOS)" : "Ultima Underworld (DOS)");
        var marker = asItIs ? null : DosGame.ReadMarker(folder);
        if (marker != null && marker.Game == t.Game)
        {
            var same = State.Targets.FirstOrDefault(x => x.Kind == TargetKind.Dos && x.Id == marker.Id);
            if (same != null) throw new InvalidOperationException("the manager already has this folder, as " + same.Name + " (" + same.Folder + ")");
            string? foreign = ForeignMarker(folder);
            if (foreign != null) throw new InvalidOperationException(foreign);
            t.Id = marker.Id;
            t.Baseline = new Dictionary<string, string>(marker.Baseline, StringComparer.Ordinal);
            t.Written = new Dictionary<string, string>(marker.Written, StringComparer.Ordinal);
            t.Displaced = new Dictionary<string, string>(marker.Displaced, StringComparer.Ordinal);
            t.ActiveMod = marker.ActiveMod;
            t.UltimaHacks = marker.UltimaHacks;
        }
        else
        {
            DosGame.Adopt(t, Vault);
            DosGame.WriteMarker(t);
        }
        State.Targets.Add(t);
        Save();
        return t;
    }

    /// <summary>Why a folder's marker cannot be used (it was taken over by a manager whose vault
    /// this one does not have), or null.</summary>
    public string? ForeignMarker(string folder)
    {
        var marker = DosGame.ReadMarker(Path.GetFullPath(folder));
        if (marker == null) return null;
        bool missing = marker.Baseline.Values.Concat(marker.Displaced.Values).Any(sha => !Vault.Has(sha));
        if (!missing) return null;
        return "A mod manager took this folder over before, with originals this one does not have (another manager folder, or one that was lost)" +
            (marker.Written.Count > 0 ? "; the folder has a mod in place (" + (marker.ActiveMod ?? "?") + ")" : "") +
            ". Use the manager folder it was added with (UWMM_HOME), or take the folder's files as they are now as the originals.";
    }

    /// <summary>Adds an Exhumed build. The game folder is the one given, else the one the player's
    /// own home remembers (data= in its settings).</summary>
    public Target AddExhumed(string portExe, string? folder = null, string? name = null)
    {
        portExe = Path.GetFullPath(portExe);
        string game = Path.GetFileName(portExe).ToLowerInvariant().Contains("uw2") ? "uw2" : "uw1";
        if (folder == null)
        {
            string cfg = Path.Combine(ExhumedGame.NormalHome(game), ExhumedGame.ConfigName(game));
            if (File.Exists(cfg)) folder = File.ReadAllLines(cfg).Where(l => l.StartsWith("data=")).Select(l => l[5..]).LastOrDefault();
            if (folder == null) throw new InvalidOperationException("give the game folder (the port's own settings do not name one)");
        }
        folder = Path.GetFullPath(folder);
        var kind = DosGame.Detect(folder);
        if (kind != null) game = kind == GameKind.UW2 ? "uw2" : "uw1";
        var t = new Target { Kind = TargetKind.Exhumed, Folder = folder, PortExe = portExe, Game = game };
        t.Name = name ?? (game == "uw2" ? "Ultima Underworld II (Exhumed)" : "Ultima Underworld (Exhumed)");
        t.Features = ExhumedGame.Probe(portExe);
        State.Targets.Add(t);
        Save();
        return t;
    }

    /// <summary>Forgets a target. A DOS folder must hold the original game first (otherwise
    /// the originals of the files a mod changed would only be in the vault, unreachable).</summary>
    public void Remove(Target t)
    {
        if (t.Kind == TargetKind.Dos && t.Written.Count > 0)
            throw new InvalidOperationException(t.Name + " has a mod in place: put the original game back first");
        if (t.Kind == TargetKind.Dos && Directory.Exists(t.Folder)) DosGame.DeleteMarker(t);
        State.Targets.Remove(t);
        Save();
    }

    public Target Get(string idOrName) => State.Find(idOrName) ?? throw new KeyNotFoundException("no target " + idOrName);

    /// <summary>For a DOS target: whether its files are the GOG release's (and which are not).</summary>
    public (bool gog, List<string> differ, List<string> missing) CompareWithGog(Target t)
    {
        var differ = KnownFiles.Differences(t.GameKind, DosGame.Originals(t, Vault), out var missing);
        return (differ.Count == 0 && missing.Count == 0, differ, missing);
    }

    // ---- mods on a target

    public ModPackage? LoadMod(string? key) => key == null ? null : Library.Load(key);

    /// <summary>What switching a DOS target to <paramref name="modKey"/> (null: the original game) writes.</summary>
    public ApplyPlan PlanDos(Target t, string? modKey, bool ultimaHacks) => DosGame.Plan(t, Vault, LoadMod(modKey), ultimaHacks);

    /// <summary>Switches a DOS target to a mod (or to the original game), through the journal.</summary>
    public ApplyPlan UseDos(Target t, string? modKey, bool ultimaHacks, bool overwrite = false)
    {
        var plan = PlanDos(t, modKey, ultimaHacks);
        if (plan.OutsideChanges.Count > 0 && !overwrite)
            throw new ModApplyException("these files changed since the manager last wrote them: " + string.Join(", ", plan.OutsideChanges) +
                ". Capture them as a mod first, or overwrite them.");
        DosGame.Apply(plan, Vault, Paths.JournalFile, Save, overwrite);
        return plan;
    }

    /// <summary>Undoes a change of a DOS folder that was interrupted (the program stopped, the
    /// computer went down): its files go back to what they were, from the vault. Returns a line for
    /// the player, or null when there was nothing to do.</summary>
    public string? Recover()
    {
        foreach (var d in State.Targets.Where(x => x.Kind == TargetKind.Dos)) DosGame.CleanTemporaries(d);
        if (!File.Exists(Paths.JournalFile)) return null;
        DosJournal j;
        try { j = DosJournal.Load(Paths.JournalFile); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException)
        {
            File.Move(Paths.JournalFile, Paths.JournalFile + ".unreadable", true);
            return "The record of an interrupted change could not be read (kept as journal.json.unreadable); check the game with Status.";
        }
        var t = State.Find(j.Target);
        string? r = t == null ? null : DosGame.Recover(t, Vault, j);
        File.Delete(Paths.JournalFile);
        return r;
    }

    /// <summary>The original of a file an Exhumed target reads: from the vault when a DOS target
    /// has the same folder (whose files may carry a DOS mod), else from the folder.</summary>
    public Func<string, byte[]?> OriginalsFor(Target t)
    {
        var dos = State.Targets.FirstOrDefault(d => d.Kind == TargetKind.Dos && SamePath(d.Folder, t.Folder));
        if (t.Kind == TargetKind.Dos || dos != null) { var d = t.Kind == TargetKind.Dos ? t : dos!; return rel => DosGame.Original(d, Vault, rel); }
        var folder = new GameFolder(t.Folder);
        return rel => folder.Read(rel);
    }

    /// <summary>Files of the game folder that a DOS mod changed in place, which an Exhumed home hides.</summary>
    public IEnumerable<string> ShieldFor(Target t)
    {
        foreach (var d in State.Targets.Where(d => d.Kind == TargetKind.Dos && SamePath(d.Folder, t.Folder)))
            foreach (var rel in d.Written.Keys) yield return rel;
    }

    /// <summary>Makes an Exhumed target's home for a mod (or the original game) ready; returns the home.</summary>
    public string UseExhumed(Target t, string? modKey)
    {
        if (t.Features == null && t.PortExe != null) t.Features = ExhumedGame.Probe(t.PortExe);
        string home = ExhumedGame.Prepare(Paths, t, LoadMod(modKey), OriginalsFor(t), ShieldFor(t).ToList());
        Save();
        return home;
    }

    /// <summary>Why a mod cannot be used on a target, or empty.</summary>
    public List<string> Problems(Target t, ModManifest m)
    {
        var r = new List<string>();
        if (m.Game != t.Game) r.Add("it is for " + m.GameName);
        if (m.Components.Any(c => c is SaveComponent)) r.Add("it is a saved game: install it into a slot");
        if (t.Kind == TargetKind.Exhumed) r.AddRange(ExhumedGame.Unsupported(m, t.Features, OriginalsFor(t)));
        return r;
    }

    /// <summary>Things to know before using a mod that do not stop it: files it was made from that
    /// are not this game's (its changes still apply where the parts it changes are the same).</summary>
    public List<string> Notes(Target t, ModManifest m)
    {
        var r = new List<string>();
        Func<string, byte[]?> orig = OriginalsFor(t);
        foreach (var kv in m.BaseFiles)
        {
            if (kv.Key == t.ExeName) continue;
            if (Files.Sha(orig(kv.Key)) != kv.Value)
                r.Add(kv.Key + " is not the one the mod was made from" + (m.BaseSet.StartsWith("gog") ? " (it was made from the GOG release)" : "") +
                      ": the parts it changes must still be the same, or it will not install");
        }
        return r;
    }

    // ---- starting the game

    public ProcessStartInfo LaunchInfo(Target t, bool useRecommended = true)
    {
        if (t.Kind == TargetKind.Dos) return Launcher.DosBox(Paths, State.Settings, t);
        string home = UseExhumed(t, t.ActiveMod);
        var enh = new List<string>(t.Enhancements);
        if (useRecommended && t.ActiveMod != null) enh.AddRange(Library.Find(t.ActiveMod)?.Manifest.ExhumedRecommended ?? new List<string>());
        return Launcher.Exhumed(t, home, enh);
    }

    public Process? Launch(Target t) => Process.Start(LaunchInfo(t));

    // ---- saves

    /// <summary>Where a target's saved games are: the DOS folder, or the Exhumed home of its current mod.</summary>
    public string SavesFolder(Target t) => t.Kind == TargetKind.Dos ? t.Folder : ExhumedGame.HomeFor(Paths, t, t.ActiveMod);

    public IFileSource GameFiles(Target t) => t.Kind == TargetKind.Dos ? new GameFolder(t.Folder) : new OverlaySource(SavesFolder(t), t.Folder);

    /// <summary>The home over the game folder, as Exhumed reads files.</summary>
    sealed class OverlaySource : IFileSource
    {
        readonly GameFolder home, data;
        public OverlaySource(string home, string data) { this.home = new GameFolder(home); this.data = new GameFolder(data); }
        public IEnumerable<string> List() => home.List().Concat(data.List()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        public byte[]? Read(string rel) => home.Read(rel) ?? data.Read(rel);
    }

    // ---- capturing changes

    /// <summary>The changes of a DOS folder against its originals, as a package (not yet in the library).</summary>
    public (ModPackage? pkg, ModDiffResult diff) Capture(Target t, ModManifest details)
    {
        if (t.Kind != TargetKind.Dos) throw new InvalidOperationException("changes are captured from a DOS folder");
        var original = DosGame.Originals(t, Vault);
        var diff = ModDiff.Compare(original, new GameFolder(t.Folder), t.GameKind);
        if (diff.Problems.Count > 0 || diff.Components.Count == 0) return (null, diff);
        details.Game = t.Game;
        if (string.IsNullOrEmpty(details.MadeWith)) details.MadeWith = "Ultima Underworlds Mod Manager (capture)";
        var pkg = ModBuilder.FromDiff(diff, details, original);
        var bad = ModBuilder.Verify(pkg, original, new GameFolder(t.Folder));
        if (bad.Count > 0) { diff.Problems.AddRange(bad); return (null, diff); }
        return (pkg, diff);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
