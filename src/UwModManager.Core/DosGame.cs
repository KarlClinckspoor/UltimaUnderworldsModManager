using UltimateEditor.Games;
using UltimateEditor.Modding;

namespace UwModManager.Core;

/// <summary>What applying a mod (or none) to a target will write.</summary>
public sealed class ApplyPlan
{
    public required Target Target { get; init; }
    public string? Mod { get; init; }
    public bool UltimaHacks { get; init; }
    /// <summary>Relative path to new bytes, or to null for a file to remove: what differs from the folder now.</summary>
    public SortedDictionary<string, byte[]?> Writes { get; } = new(StringComparer.Ordinal);
    /// <summary>Every file the target ends up with that is not the original, by hash (already there or written).</summary>
    public Dictionary<string, string> Result { get; } = new(StringComparer.Ordinal);
    /// <summary>Files the plan would overwrite that changed since the manager last wrote them (or the original, if it never did).</summary>
    public List<string> OutsideChanges { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>The change being written to a DOS folder: each file's hash before (kept in the vault) and after.</summary>
public sealed class DosJournal
{
    public sealed class Entry
    {
        public string File { get; set; } = "";
        /// <summary>The hash of what was there (in the vault), or null: no file.</summary>
        public string? Before { get; set; }
        public string? After { get; set; }
    }
    public string Target { get; set; } = "";
    public string Stamp { get; set; } = "";
    public string? Mod { get; set; }
    public bool UltimaHacks { get; set; }
    public List<Entry> Files { get; set; } = new();

    public void Save(string path) => UwModManager.Core.Files.WriteAtomic(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this));
    public static DosJournal Load(string path) => System.Text.Json.JsonSerializer.Deserialize<DosJournal>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("empty journal");
}

/// <summary>What a DOS folder the manager took over says about itself (DosGame.MarkerName).</summary>
public sealed class FolderMarker
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string? ActiveMod { get; set; }
    public bool UltimaHacks { get; set; }
    public Dictionary<string, string> Baseline { get; set; } = new();
    public Dictionary<string, string> Written { get; set; } = new();
    public Dictionary<string, string> Displaced { get; set; } = new();
}

/// <summary>
/// The DOS game in its folder, changed in place. The first time the manager touches a folder it
/// keeps every game file in the vault (the editor's .BKP backups count as the originals), and from
/// then on the folder always holds either the originals or the originals with one mod applied.
/// A file changed by something else (the editor, by hand) is never overwritten without asking.
/// </summary>
public static class DosGame
{
    /// <summary>The game of a folder holding UW.EXE or UW2.EXE and DATA, else null.</summary>
    public static GameKind? Detect(string folder)
    {
        var g = new GameFolder(folder);
        string? data = g.Resolve("DATA");
        if (data == null || !Directory.Exists(data)) return null;
        if (g.Resolve("UW2.EXE") != null) return GameKind.UW2;
        if (g.Resolve("UW.EXE") != null) return GameKind.UW1;
        return null;
    }

    /// <summary>GOG's DOSBox setups in the folder that run the game from its CD image (game.gog), which never sees files changed in the folder.</summary>
    public static List<string> CdImageConfs(string folder)
    {
        var r = new List<string>();
        foreach (var conf in Directory.GetFiles(folder, "*.conf"))
            foreach (var line in File.ReadAllLines(conf))
            {
                string l = line.Trim();
                if (l.StartsWith("imgmount", StringComparison.OrdinalIgnoreCase) && l.Contains("game.gog", StringComparison.OrdinalIgnoreCase))
                { r.Add(Path.GetFileName(conf)); break; }
            }
        return r;
    }

    /// <summary>Takes the folder over: every game file's original into the vault (a .BKP the editor made counts as the original).</summary>
    public static void Adopt(Target t, Vault vault)
    {
        var orig = new BackupBaseFolder(t.Folder);
        t.Baseline.Clear();
        foreach (var rel in orig.List())
        {
            var b = orig.Read(rel);
            if (b != null) t.Baseline[rel] = vault.Put(b);
        }
    }

    public static byte[]? Original(Target t, Vault vault, string rel)
    {
        rel = GameFolder.Normalize(rel);
        return t.Baseline.TryGetValue(rel, out var sha) ? vault.Get(sha) : null;
    }

    /// <summary>What the folder holds without a mod: the original, or for a file that is not one,
    /// what was there before a mod overwrote it (or nothing).</summary>
    static string? UnmoddedSha(Target t, string rel) =>
        t.Baseline.TryGetValue(rel, out var b) ? b : t.Displaced.TryGetValue(rel, out var d) ? d : null;

    /// <summary>The originals as a file source (for diffs and for mods' bases).</summary>
    public static IFileSource Originals(Target t, Vault vault) => new VaultSource(t, vault);

    sealed class VaultSource : IFileSource
    {
        readonly Target t; readonly Vault v;
        public VaultSource(Target t, Vault v) { this.t = t; this.v = v; }
        public IEnumerable<string> List() => t.Baseline.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        public byte[]? Read(string rel) => Original(t, v, rel);
    }

    /// <summary>What the target must look like with <paramref name="mod"/> (null: the original game) and UltimaHacks or not.</summary>
    public static ApplyPlan Plan(Target t, Vault vault, ModPackage? mod, bool ultimaHacks)
    {
        if (t.Baseline.Count == 0) throw new InvalidOperationException("the folder has not been taken over yet");
        var plan = new ApplyPlan { Target = t, Mod = mod == null ? null : Library.KeyOf(mod.Manifest), UltimaHacks = ultimaHacks };
        var desired = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (mod != null)
        {
            if (mod.Manifest.Game != t.Game) throw new ModApplyException("the mod is for " + mod.Manifest.GameName + ", this is " + (t.Game == "uw2" ? "Ultima Underworld II" : "Ultima Underworld"));
            if (mod.Manifest.Components.Any(c => c is SaveComponent))
                throw new ModApplyException("a saved game is installed into a save slot, not applied to the game (Saves)");
            foreach (var kv in ModApply.Files(mod, rel => Original(t, vault, rel))) desired[kv.Key] = kv.Value;
            plan.Warnings.AddRange(ModClassifier.Warnings(mod.Manifest));
        }
        if (ultimaHacks || (mod != null && ModApply.ChangesExe(mod.Manifest)))
        {
            byte[] exe = Original(t, vault, t.ExeName) ?? throw new ModApplyException(t.ExeName + " has no original in the vault");
            desired[t.ExeName] = Hacks.BuildExe(exe, t.GameKind, ultimaHacks, mod);
        }
        // what an earlier mod wrote and this one does not: back to the original
        foreach (var rel in t.Written.Keys)
            if (!desired.ContainsKey(rel)) { string? u = UnmoddedSha(t, rel); desired[rel] = u == null ? null : vault.Get(u); }
        var folder = new GameFolder(t.Folder);
        foreach (var kv in desired.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string want = Files.Sha(kv.Value);
            if (!GameFolder.IsModFile(kv.Key)) throw new ModApplyException("refused to write " + kv.Key + ", which is not a game file");
            string? unmodded = UnmoddedSha(t, kv.Key);
            bool isOriginal = unmodded != null ? unmodded == want : kv.Value == null;
            if (!isOriginal) plan.Result[kv.Key] = want;
            byte[]? cur = folder.Read(kv.Key);
            if (Same(cur, kv.Value)) continue;
            string curSha = Files.Sha(cur);
            bool known = t.Written.TryGetValue(kv.Key, out var w) ? w == curSha
                : t.Baseline.TryGetValue(kv.Key, out var b) ? b == curSha : cur == null;
            if (!known) plan.OutsideChanges.Add(kv.Key);
            plan.Writes[kv.Key] = kv.Value;
        }
        return plan;
    }

    /// <summary>
    /// Writes the plan as one change: what each file holds now goes into the vault and a journal
    /// first, then the files are written (each through a temporary file and a rename). If a write
    /// fails, the files written so far are put back and the error is passed on; if the program
    /// stops, <see cref="Recover"/> puts them back at the next start. <paramref name="commit"/>
    /// saves the state, after which the change is done. Refuses outside changes unless
    /// <paramref name="overwrite"/> (their contents are kept in the vault all the same).
    /// </summary>
    public static void Apply(ApplyPlan plan, Vault vault, string journalPath, Action commit, bool overwrite = false)
    {
        var t = plan.Target;
        if (plan.OutsideChanges.Count > 0 && !overwrite)
            throw new ModApplyException("these files changed since the manager last wrote them: " + string.Join(", ", plan.OutsideChanges) +
                ". Keep the changes as a mod first (Capture), or overwrite them.");
        var folder = new GameFolder(t.Folder);
        var j = new DosJournal { Target = t.Id, Stamp = Guid.NewGuid().ToString("N"), Mod = plan.Mod, UltimaHacks = plan.UltimaHacks };
        foreach (var kv in plan.Writes)
        {
            if (!GameFolder.IsModFile(kv.Key)) throw new ModApplyException("refused to write " + kv.Key + ", which is not a game file");
            byte[]? cur = folder.Read(kv.Key);
            j.Files.Add(new DosJournal.Entry { File = kv.Key, Before = cur == null ? null : vault.Put(cur), After = kv.Value == null ? null : Files.Sha(kv.Value) });
        }
        j.Save(journalPath);
        try
        {
            foreach (var kv in plan.Writes)
            {
                string path = PathOf(t, folder, kv.Key);
                if (kv.Value == null) { if (File.Exists(path)) File.Delete(path); }
                else Files.WriteAtomic(path, kv.Value);
            }
        }
        catch
        {
            try { Rollback(t, vault, j); File.Delete(journalPath); }
            catch { /* the journal stays: Recover tries again at the next start */ }
            throw;
        }
        var now = DateTime.UtcNow;
        foreach (var e in j.Files)
        {
            if (e.Before == null || !plan.OutsideChanges.Contains(e.File)) continue;
            if (t.Baseline.ContainsKey(e.File)) t.Overwritten.Add(new OverwrittenFile { File = e.File, Sha = e.Before, When = now });
            else if (!t.Displaced.ContainsKey(e.File)) t.Displaced[e.File] = e.Before;
        }
        // a displaced file that is back no longer needs remembering
        foreach (var e in j.Files)
            if (!t.Baseline.ContainsKey(e.File) && t.Displaced.TryGetValue(e.File, out var d) && d == e.After) t.Displaced.Remove(e.File);
        // what the folder now holds that is not the original
        t.Written = new Dictionary<string, string>(plan.Result, StringComparer.Ordinal);
        t.ActiveMod = plan.Mod;
        t.UltimaHacks = plan.UltimaHacks;
        t.LastChange = j.Stamp;
        WriteMarker(t);
        commit();
        File.Delete(journalPath);
    }

    /// <summary>After an interruption: the change in the journal is undone (its files back to what
    /// they were), unless the state shows it finished. Returns a line for the player, or null.</summary>
    public static string? Recover(Target t, Vault vault, DosJournal j)
    {
        CleanTemporaries(t);
        if (t.LastChange == j.Stamp) return null;
        Rollback(t, vault, j);
        WriteMarker(t);
        return string.Format("A change of {0} to {1} was interrupted and has been undone: it has {2} again.",
            t.Name, j.Mod ?? "the original game", t.ActiveMod ?? "the original game");
    }

    static void Rollback(Target t, Vault vault, DosJournal j)
    {
        var folder = new GameFolder(t.Folder);
        foreach (var e in j.Files)
        {
            string path = PathOf(t, folder, e.File);
            byte[]? cur = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (Files.Sha(cur) == (e.Before ?? Files.Sha(null))) continue;
            if (e.Before == null) File.Delete(path);
            else Files.WriteAtomic(path, vault.Get(e.Before));
        }
    }

    static string PathOf(Target t, GameFolder folder, string rel)
    {
        if (!GameFolder.IsModFile(rel)) throw new ModApplyException("refused to write " + rel + ", which is not a game file");
        return GameFolder.Inside(t.Folder, folder.Resolve(rel) ?? folder.PathFor(rel));
    }

    /// <summary>Temporary files an interrupted write left in the game folder.</summary>
    public static void CleanTemporaries(Target t)
    {
        if (!Directory.Exists(t.Folder)) return;
        foreach (var f in Directory.EnumerateFiles(t.Folder, "*", SearchOption.AllDirectories).ToList())
            if (f.EndsWith(".uwmm-tmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".uwmod-tmp", StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (IOException) { }
    }

    // ---- the marker: the folder says which target it is, and what its originals are

    public const string MarkerName = ".uwmm-target.json";

    static readonly System.Text.Json.JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    /// <summary>Writes what the manager knows of the folder into it, so that taking it over again
    /// (after the manager forgot it, lost its state, or with another path to the same folder)
    /// starts from the real originals, not from files a mod changed.</summary>
    public static void WriteMarker(Target t)
    {
        var m = new FolderMarker
        {
            Id = t.Id, Game = t.Game, ActiveMod = t.ActiveMod, UltimaHacks = t.UltimaHacks,
            Baseline = t.Baseline, Written = t.Written, Displaced = t.Displaced,
        };
        Files.WriteAtomic(Path.Combine(t.Folder, MarkerName), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, MarkerJson));
    }

    public static FolderMarker? ReadMarker(string folder)
    {
        string p = Path.Combine(folder, MarkerName);
        if (!File.Exists(p)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<FolderMarker>(File.ReadAllBytes(p)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static void DeleteMarker(Target t)
    {
        string p = Path.Combine(t.Folder, MarkerName);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Game files that are neither the original nor what the manager wrote: changes made by something else.</summary>
    public static List<string> LocalChanges(Target t)
    {
        var r = new List<string>();
        var folder = new GameFolder(t.Folder);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in folder.List())
        {
            seen.Add(rel);
            string sha = Files.Sha(folder.Read(rel));
            string? expect = t.Written.TryGetValue(rel, out var w) ? w : t.Baseline.TryGetValue(rel, out var b) ? b : null;
            if (expect != sha) r.Add(rel);
        }
        foreach (var rel in t.Baseline.Keys)
            if (!seen.Contains(rel) && !t.Written.ContainsKey(rel)) r.Add(rel + " (removed)");
        return r;
    }

    static bool Same(byte[]? a, byte[]? b) => a == null || b == null ? a == b : ModDiff.Equal(a, b);
}
