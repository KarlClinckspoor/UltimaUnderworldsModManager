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
        string dst = FileFor(KeyOf(pkg.Manifest));
        Files.WriteAtomic(dst, bytes);
        return new LibraryEntry { Key = KeyOf(pkg.Manifest), Path = dst, Manifest = pkg.Manifest, Size = bytes.Length };
    }

    public void Add(ModPackage pkg)
    {
        Files.WriteAtomic(FileFor(KeyOf(pkg.Manifest)), pkg.ToBytes());
    }

    public void Remove(string key)
    {
        var e = Find(key);
        if (e != null) File.Delete(e.Path);
    }

    /// <summary>The package file of a key: the key's own name, or, when that file holds another
    /// key (names that differ only in characters a file name cannot have), the name with the key's hash.</summary>
    string FileFor(string key)
    {
        var existing = List().FirstOrDefault(e => e.Key == key);
        if (existing != null) return existing.Path;
        string plain = System.IO.Path.Combine(root, Safe(key) + ModPackage.Extension);
        if (!File.Exists(plain)) return plain;
        return System.IO.Path.Combine(root, Safe(key) + "-" + Files.Sha(System.Text.Encoding.UTF8.GetBytes(key))[7..15] + ModPackage.Extension);
    }

    static string Safe(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s;
    }
}
