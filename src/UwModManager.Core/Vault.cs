namespace UwModManager.Core;

/// <summary>
/// The originals: every file of a game folder the manager took over, kept by content
/// (vault/ab/abcdef...), so that the game can always be put back exactly as it was, and mods
/// are always applied to the files they were made from.
/// </summary>
public sealed class Vault
{
    readonly string root;
    public Vault(string root) { this.root = root; Directory.CreateDirectory(root); }

    static string Hex(string sha) => sha.StartsWith("sha256:") ? sha[7..] : sha;
    string PathOf(string sha) { string h = Hex(sha); return Path.Combine(root, h[..2], h); }

    public bool Has(string sha) => File.Exists(PathOf(sha));

    public string Put(byte[] data)
    {
        string sha = Files.Sha(data);
        string p = PathOf(sha);
        if (!File.Exists(p)) Files.WriteAtomic(p, data);
        return sha;
    }

    public byte[] Get(string sha)
    {
        string p = PathOf(sha);
        if (!File.Exists(p)) throw new FileNotFoundException("the vault has no copy of " + sha, p);
        byte[] b = File.ReadAllBytes(p);
        if (Files.Sha(b) != sha) throw new InvalidDataException("the vault's copy of " + sha + " is damaged");
        return b;
    }
}
