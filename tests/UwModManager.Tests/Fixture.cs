using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UwModManager.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UwModManager.Tests;

/// <summary>
/// The game files: UW_GAMEDATA (a folder with UW and/or UW2), else ../UltimaUnderworldEditorUnity/gamedata.
/// A game that is not there is skipped. Each test gets fresh copies (with the editor's .BKP
/// originals put back) and a manager folder of its own.
/// </summary>
public static class Env
{
    public static string Root
    {
        get
        {
            string e = Environment.GetEnvironmentVariable("UW_GAMEDATA");
            if (!string.IsNullOrEmpty(e)) return e;
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return d == null ? "" : Path.Combine(d.FullName, "..", "UltimaUnderworldEditorUnity", "gamedata");
        }
    }

    public static IEnumerable<object[]> Games()
    {
        bool any = false;
        foreach (var g in new[] { "UW", "UW2" })
            if (Directory.Exists(Path.Combine(Root, g, "DATA"))) { any = true; yield return new object[] { g }; }
        if (!any) yield return new object[] { "none" };
    }

    public static string Copy(string game)
    {
        string src = Path.Combine(Root, game);
        if (!Directory.Exists(Path.Combine(src, "DATA"))) return null;
        string dst = Temp(game);
        CopyDir(src, dst);
        foreach (var bkp in Directory.GetFiles(dst, "*.BKP", SearchOption.AllDirectories))
        {
            File.Copy(bkp, bkp[..^4], true);
            File.Delete(bkp);
        }
        return dst;
    }

    public static string Temp(string name)
    {
        string d = Path.Combine(Path.GetTempPath(), "uwmm-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static GameKind Kind(string game) => game == "UW2" ? GameKind.UW2 : GameKind.UW1;

    /// <summary>A mod made the editor's way: change a copy, diff it against the original, package it.</summary>
    public static ModPackage MakeMod(string game, Action<string> change, string id = "test.mod", string version = "1.0.0")
    {
        string orig = Copy(game), mod = Copy(game);
        change(mod);
        var diff = ModDiff.Compare(new GameFolder(orig), new GameFolder(mod), Kind(game));
        Assert.Empty(diff.Problems);
        var m = new ModManifest { Id = id, Name = "Test " + id, Version = version, Game = game == "UW2" ? "uw2" : "uw1", Authors = { "Tests" } };
        var pkg = ModBuilder.FromDiff(diff, m, new GameFolder(orig));
        return ModPackage.FromBytes(pkg.ToBytes());
    }

    public static void EditLevel(string folder, GameKind kind, int level, int at, byte xor)
    {
        var g = new GameFolder(folder);
        string lev = g.Resolve("DATA/LEV.ARK");
        var ar = Archive.Load(lev, kind);
        int b = GameProfile.For(kind).LevelBlock(level);
        byte[] blk = ar.Read(b);
        blk[at] ^= xor;
        ar.Write(b, blk);
        ar.Save(lev);
    }

    public static void EditString(string folder, int block, int index, string text)
    {
        var g = new GameFolder(folder);
        string p = g.Resolve("DATA/STRINGS.PAK");
        var s = StringsPakFile.Parse(File.ReadAllBytes(p));
        s.Find(block).Strings[index] = text;
        File.WriteAllBytes(p, s.Build(new[] { block }));
    }

    public static void SetStart(string folder, GameKind kind, int x, int y)
    {
        var g = new GameFolder(folder);
        string p = g.Resolve(kind == GameKind.UW2 ? "UW2.EXE" : "UW.EXE");
        byte[] exe = File.ReadAllBytes(p);
        Assert.True(StartPosition.Patch(exe, kind, x, y));
        File.WriteAllBytes(p, exe);
    }

    public static byte[] Read(string folder, string rel) => new GameFolder(folder).Read(rel);
}
