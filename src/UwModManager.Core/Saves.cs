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

    /// <summary>Backups are kept per saves folder (a DOS game folder, or an Exhumed home), so a
    /// slot is only offered its own game's.</summary>
    static string BackupDir(AppPaths paths, string savesFolder) =>
        Path.Combine(paths.Root, "save-backups", Files.Sha(Encoding.UTF8.GetBytes(Path.GetFullPath(savesFolder).TrimEnd('/', '\\')))[7..19]);

    /// <summary>The slot's files into the vault; a backup file (JSON: name to hash) next to the
    /// manager's state, returned. Null for an empty slot.</summary>
    public static string? Backup(AppPaths paths, Vault vault, SaveSlot slot, string label)
    {
        if (!Directory.Exists(slot.Folder)) return null;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(slot.Folder)) files[Path.GetFileName(f)] = vault.Put(File.ReadAllBytes(f));
        if (files.Count == 0) return null;
        string dir = BackupDir(paths, Path.GetDirectoryName(Path.GetFullPath(slot.Folder))!);
        Directory.CreateDirectory(dir);
        var o = new Dictionary<string, object> { ["slot"] = slot.Number, ["folder"] = slot.Folder, ["description"] = slot.Description, ["label"] = label, ["files"] = files };
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        for (int n = 0; ; n++)
        {
            // never over another backup: the time, then a counter
            string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + (n > 0 ? "-" + n : "") + "-" + Safe(label) + "-SAVE" + slot.Number + ".json");
            try
            {
                using var f = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
                f.Write(json, 0, json.Length);
                return file;
            }
            catch (IOException) when (File.Exists(file) && n < 100) { }
        }
    }

    /// <summary>The backups of the slots of one saves folder, newest first.</summary>
    public static IReadOnlyList<string> Backups(AppPaths paths, string savesFolder, int? slot = null)
    {
        string dir = BackupDir(paths, savesFolder);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.json")
            .Where(f => slot == null || f.EndsWith("-SAVE" + slot + ".json", StringComparison.Ordinal))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Puts a backup back into a slot folder (the slot's current files are backed up first).
    /// Every file is read from the vault before the slot is touched.</summary>
    public static void Restore(AppPaths paths, Vault vault, string backupFile, SaveSlot into)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(backupFile));
        var files = new List<(string name, byte[] data)>();
        foreach (var p in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            string name = p.Name;
            if (name.Length == 0 || name != Path.GetFileName(name) || name is "." or "..") throw new InvalidDataException("the backup names a file " + name);
            files.Add((name, vault.Get(p.Value.GetString()!)));
        }
        Backup(paths, vault, into, "before-restore");
        Directory.CreateDirectory(into.Folder);
        foreach (var f in Directory.GetFiles(into.Folder)) File.Delete(f);
        foreach (var (name, data) in files) Files.WriteAtomic(Path.Combine(into.Folder, name), data);
    }

    /// <summary>Installs a saved-game mod into a slot (backing the slot up first). Returns the
    /// problems found against the game's files, which do not stop the install.</summary>
    public static List<string> Install(AppPaths paths, Vault vault, ModPackage pkg, SaveSlot into, IFileSource game, GameKind kind)
    {
        var c = pkg.Manifest.Components.OfType<SaveComponent>().FirstOrDefault() ?? throw new ModApplyException("the mod is not a saved game");
        var problems = SaveMods.Check(c, pkg, game, kind);
        var bad = pkg.Check();
        if (bad.Count > 0) throw new ModApplyException("the package is not usable: " + string.Join("; ", bad));
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
