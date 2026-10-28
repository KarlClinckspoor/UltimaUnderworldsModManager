using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;

namespace UwModManager.Core;

public sealed class SaveSlot
{
    public int Number { get; init; }
    public required string Folder { get; init; }
    public bool Exists { get; init; }
    public string Description { get; init; } = "";
    public DateTime Written { get; init; }
    public override string ToString() => "SAVE" + Number + (Exists ? ": " + (Description.Length > 0 ? Description : "(no description)") : ": empty");
}

/// <summary>
/// Saved games: the SAVE1-SAVE4 folders of a DOS game folder, or of an Exhumed home. Before a slot
/// is replaced, its files go into the vault and a backup record, so it can be put back.
/// </summary>
public static class Saves
{
    public static IReadOnlyList<SaveSlot> List(string folder)
    {
        var r = new List<SaveSlot>();
        var g = new GameFolder(folder);
        for (int n = 1; n <= 4; n++)
        {
            string? dir = g.Resolve("SAVE" + n);
            string path = dir ?? Path.Combine(folder, "SAVE" + n);
            string? lev = dir == null ? null : new GameFolder(dir).Resolve("LEV.ARK");
            string? desc = dir == null ? null : new GameFolder(dir).Resolve("DESC");
            r.Add(new SaveSlot
            {
                Number = n, Folder = path, Exists = lev != null,
                Description = desc != null ? Encoding.ASCII.GetString(File.ReadAllBytes(desc)).TrimEnd('\0', ' ', '\r', '\n') : "",
                Written = lev != null ? File.GetLastWriteTime(lev) : DateTime.MinValue,
            });
        }
        return r;
    }

    /// <summary>The slot's files into the vault; a backup file (JSON: name to hash) next to the
    /// manager's state, returned. Null for an empty slot.</summary>
    public static string? Backup(AppPaths paths, Vault vault, SaveSlot slot, string label)
    {
        if (!Directory.Exists(slot.Folder)) return null;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(slot.Folder)) files[Path.GetFileName(f)] = vault.Put(File.ReadAllBytes(f));
        if (files.Count == 0) return null;
        string dir = Path.Combine(paths.Root, "save-backups");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Safe(label) + "-SAVE" + slot.Number + ".json");
        var o = new Dictionary<string, object> { ["slot"] = slot.Number, ["folder"] = slot.Folder, ["description"] = slot.Description, ["files"] = files };
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    public static IReadOnlyList<string> Backups(AppPaths paths)
    {
        string dir = Path.Combine(paths.Root, "save-backups");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).ToList() : new List<string>();
    }

    /// <summary>Puts a backup back into a slot folder (the slot's current files are backed up first).</summary>
    public static void Restore(AppPaths paths, Vault vault, string backupFile, SaveSlot into)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(backupFile));
        Backup(paths, vault, into, "before-restore");
        Directory.CreateDirectory(into.Folder);
        foreach (var f in Directory.GetFiles(into.Folder)) File.Delete(f);
        foreach (var p in doc.RootElement.GetProperty("files").EnumerateObject())
            Files.WriteAtomic(Path.Combine(into.Folder, p.Name), vault.Get(p.Value.GetString()!));
    }

    /// <summary>Installs a saved-game mod into a slot (backing the slot up first). Returns the
    /// problems found against the game's files, which do not stop the install.</summary>
    public static List<string> Install(AppPaths paths, Vault vault, ModPackage pkg, SaveSlot into, IFileSource game, GameKind kind)
    {
        var c = pkg.Manifest.Components.OfType<SaveComponent>().FirstOrDefault() ?? throw new ModApplyException("the mod is not a saved game");
        var problems = SaveMods.Check(c, pkg, game, kind);
        Backup(paths, vault, into, "before-" + pkg.Manifest.Id);
        if (Directory.Exists(into.Folder)) foreach (var f in Directory.GetFiles(into.Folder)) File.Delete(f);
        SaveMods.Install(c, pkg, into.Folder);
        return problems;
    }

    /// <summary>A slot as a saved-game mod.</summary>
    public static ModPackage Export(SaveSlot slot, IFileSource game, GameKind kind, ModManifest details)
    {
        var pkg = new ModPackage { Manifest = details };
        var c = SaveMods.Export(slot.Folder, game, kind, slot.Number, pkg.Files);
        details.Components.Clear();
        details.Components.Add(c);
        details.BaseFiles = new SortedDictionary<string, string>(c.DependsOn, StringComparer.Ordinal);
        details.BaseSet = KnownFiles.SetOf(kind, details.BaseFiles);
        if (string.IsNullOrEmpty(details.Created)) details.Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        ModClassifier.Classify(details, null, pkg);
        return pkg;
    }

    static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s.Replace('@', '-');
    }
}
