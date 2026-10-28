using UltimateEditor.Modding;

namespace UwModManager.Core;

/// <summary>A mod in the library: its package file and manifest.</summary>
public sealed class LibraryEntry
{
    public required string Key { get; init; }
    public required string Path { get; init; }
    public required ModManifest Manifest { get; init; }
    public long Size { get; init; }
    // The manifest's members are fields, which bindings can't see.
    public string Name => Manifest.Name;
    public string Version => Manifest.Version;
    public string Kind => Manifest.Type;
    public string GameName => Manifest.GameName;
    public override string ToString() => Manifest.Name + " " + Manifest.Version;
}

/// <summary>The imported mods: .uwmod files in the library folder, one per id and version.</summary>
public sealed class Library
{
    readonly string root;
    public Library(string root) { this.root = root; Directory.CreateDirectory(root); }

    public static string KeyOf(ModManifest m) => m.Id + "@" + m.Version;

    public IReadOnlyList<LibraryEntry> List()
    {
        var r = new List<LibraryEntry>();
        foreach (var f in Directory.GetFiles(root, "*" + ModPackage.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pkg = ModPackage.Load(f);
                r.Add(new LibraryEntry { Key = KeyOf(pkg.Manifest), Path = f, Manifest = pkg.Manifest, Size = new FileInfo(f).Length });
            }
            catch (Exception) { /* not a package this version reads; left alone */ }
        }
        return r;
    }

    public LibraryEntry? Find(string key) => List().FirstOrDefault(e => e.Key == key);

    public ModPackage Load(string key)
    {
        var e = Find(key) ?? throw new KeyNotFoundException("the library has no mod " + key);
        return ModPackage.Load(e.Path);
    }

    /// <summary>Copies a package into the library (after checking it); returns its entry.</summary>
    public LibraryEntry Import(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var pkg = ModPackage.FromBytes(bytes);
        var problems = pkg.Check();
        if (problems.Count > 0) throw new InvalidDataException("the package is not usable: " + string.Join("; ", problems));
        string name = Safe(pkg.Manifest.Id + "-" + pkg.Manifest.Version) + ModPackage.Extension;
        string dst = System.IO.Path.Combine(root, name);
        Files.WriteAtomic(dst, bytes);
        return new LibraryEntry { Key = KeyOf(pkg.Manifest), Path = dst, Manifest = pkg.Manifest, Size = bytes.Length };
    }

    public void Add(ModPackage pkg)
    {
        string dst = System.IO.Path.Combine(root, Safe(pkg.Manifest.Id + "-" + pkg.Manifest.Version) + ModPackage.Extension);
        Files.WriteAtomic(dst, pkg.ToBytes());
    }

    public void Remove(string key)
    {
        var e = Find(key);
        if (e != null) File.Delete(e.Path);
    }

    static string Safe(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s;
    }
}
