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
            if (!desired.ContainsKey(rel)) desired[rel] = Original(t, vault, rel);
        var folder = new GameFolder(t.Folder);
        foreach (var kv in desired.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string want = Files.Sha(kv.Value);
            bool isOriginal = t.Baseline.TryGetValue(kv.Key, out var ob) ? ob == want : kv.Value == null;
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

    /// <summary>Writes the plan (each file through a temporary file and a rename). Refuses outside
    /// changes unless <paramref name="overwrite"/>; the caller saves the state afterwards.</summary>
    public static void Apply(ApplyPlan plan, Vault vault, bool overwrite = false)
    {
        var t = plan.Target;
        if (plan.OutsideChanges.Count > 0 && !overwrite)
            throw new ModApplyException("these files changed since the manager last wrote them: " + string.Join(", ", plan.OutsideChanges) +
                ". Keep the changes as a mod first (Capture), or overwrite them.");
        var folder = new GameFolder(t.Folder);
        foreach (var kv in plan.Writes)
        {
            string path = folder.Resolve(kv.Key) ?? folder.PathFor(kv.Key);
            if (kv.Value == null) { if (File.Exists(path)) File.Delete(path); }
            else Files.WriteAtomic(path, kv.Value);
        }
        // what the folder now holds that is not the original (written now, or already there after an interruption)
        t.Written = new Dictionary<string, string>(plan.Result, StringComparer.Ordinal);
        t.ActiveMod = plan.Mod;
        t.UltimaHacks = plan.UltimaHacks;
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
