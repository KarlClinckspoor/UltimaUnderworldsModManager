using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UltimateEditor.Modding.Exe;
using UwModManager.Core;
using Xunit;

namespace UwModManager.Tests;

/// <summary>Packages are untrusted: what they name stays inside the game; and the smaller guards
/// (save backups, executable patches, library names).</summary>
public class SafetyTests
{
    static ModPackage WithFile(string file, string game = "uw1")
    {
        var pkg = new ModPackage { Manifest = new ModManifest { Id = "evil.mod", Name = "Evil", Version = "1", Game = game } };
        pkg.Manifest.Components.Add(new FileComponent { File = file, Base = Hashing.Absent, Data = "payload/x" });
        pkg.Files["payload/x"] = Encoding.ASCII.GetBytes("pwned");
        return pkg;
    }

    [Theory]
    [InlineData("DATA/../UW.EXE")]
    [InlineData("DATA/../../victim.txt")]
    [InlineData("/DATA/LEV.ARK")]
    [InlineData("DATA//LEV.ARK")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("DATA/./LEV.ARK")]
    [InlineData("SAVE1/PLAYER.DAT")]
    [InlineData("UW.CFG")]
    [InlineData("DOSBOX.CONF")]
    public void A_package_naming_a_path_outside_the_game_files_is_refused(string file)
    {
        Assert.False(GameFolder.IsModFile(file));
        var pkg = WithFile(file);
        Assert.NotEmpty(pkg.Check());
        Assert.Throws<InvalidDataException>(() => ModPackage.FromBytes(pkg.ToBytes()));
        Assert.Throws<ModApplyException>(() => ModApply.Files(pkg, _ => null));
        string lib = Env.Temp("lib"), f = Path.Combine(Env.Temp("pkg"), "evil.uwmod");
        File.WriteAllBytes(f, pkg.ToBytes());
        Assert.Throws<InvalidDataException>(() => new Library(lib).Import(f));
    }

    [Theory]
    [InlineData("DATA/LEV.ARK")]
    [InlineData("data/new.dat")]
    [InlineData("UW2.EXE")]
    [InlineData("SOUND/UW01.XMI")]
    public void Game_files_are_allowed(string file)
    {
        Assert.True(GameFolder.IsModFile(file));
        Assert.Empty(WithFile(file).PathProblems());
    }

    [Fact]
    public void Leftover_temporary_files_are_not_game_files()
    {
        Assert.False(GameFolder.IsGameFile("DATA/LEV.ARK.uwmm-tmp"));
        Assert.False(GameFolder.IsGameFile("DATA/LEV.ARK.UWMOD-TMP"));
    }

    [Fact]
    public void A_saved_game_naming_another_file_is_refused()
    {
        var pkg = new ModPackage { Manifest = new ModManifest { Id = "evil.save", Name = "Evil", Version = "1", Game = "uw1" } };
        var c = new SaveComponent { Slot = 1 };
        c.Files.Add(new SaveComponent.SaveFile { Name = "../UW.EXE", Data = "payload/s" });
        pkg.Manifest.Components.Add(c);
        pkg.Files["payload/s"] = new byte[] { 1 };
        Assert.NotEmpty(pkg.PathProblems());
        Assert.Throws<InvalidDataException>(() => ModPackage.FromBytes(pkg.ToBytes()));
        string slot = Path.Combine(Env.Temp("game"), "SAVE1");
        Assert.Throws<ModApplyException>(() => SaveMods.Install(c, pkg, slot));
        Assert.False(File.Exists(Path.Combine(slot, "..", "UW.EXE")));
    }

    [Fact]
    public void Save_backups_never_replace_each_other_and_stay_with_their_folder()
    {
        var m = new Manager(Env.Temp("mgr"));
        string a = Env.Temp("gameA"), b = Env.Temp("gameB");
        foreach (var g in new[] { a, b })
        {
            Directory.CreateDirectory(Path.Combine(g, "SAVE1"));
            File.WriteAllBytes(Path.Combine(g, "SAVE1", "LEV.ARK"), Encoding.ASCII.GetBytes(g));
        }
        var slotA = Saves.List(a)[0];
        string first = Saves.Backup(m.Paths, m.Vault, slotA, "before-restore")!;
        string second = Saves.Backup(m.Paths, m.Vault, slotA, "before-restore")!;
        Assert.NotEqual(first, second);
        Assert.Equal(2, Saves.Backups(m.Paths, a, 1).Count);
        Assert.Empty(Saves.Backups(m.Paths, b));
        Assert.Empty(Saves.Backups(m.Paths, a, 2));
    }

    [Fact]
    public void A_restore_whose_files_are_missing_leaves_the_slot_alone()
    {
        var m = new Manager(Env.Temp("mgr"));
        string g = Env.Temp("game");
        Directory.CreateDirectory(Path.Combine(g, "SAVE1"));
        File.WriteAllText(Path.Combine(g, "SAVE1", "LEV.ARK"), "current");
        string backup = Path.Combine(Env.Temp("b"), "x.json");
        File.WriteAllText(backup, "{\"files\":{\"LEV.ARK\":\"sha256:" + new string('0', 64) + "\"}}");
        Assert.Throws<FileNotFoundException>(() => Saves.Restore(m.Paths, m.Vault, backup, Saves.List(g)[0]));
        Assert.Equal("current", File.ReadAllText(Path.Combine(g, "SAVE1", "LEV.ARK")));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void Executable_patches_must_replace_bytes_one_for_one(string game)
    {
        string folder = Env.Copy(game);
        if (folder == null) return;
        var kind = Env.Kind(game);
        byte[] exe = Env.Read(folder, ExeIdentity.ExeName(kind));
        var pkg = new ModPackage { Manifest = new ModManifest { Id = "t.exe", Name = "Exe", Version = "1", Game = kind == GameKind.UW2 ? "uw2" : "uw1" } };
        var c = new ExeBytesComponent { File = ExeIdentity.ExeName(kind) };
        c.Patches.Add(new ExeBytesComponent.Patch { Segment = -1, Offset = 0, FileOffset = 0, Base = Hashing.Hex(new[] { exe[0] }), Data = "4D5A90", Before = "", After = "" });
        pkg.Manifest.Components.Add(c);
        Assert.Throws<ModApplyException>(() => Hacks.BuildExe(exe, kind, false, pkg));
        c.Patches[0].Base = ""; c.Patches[0].Data = "";
        Assert.Throws<ModApplyException>(() => Hacks.BuildExe(exe, kind, false, pkg));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void An_executable_that_already_has_UltimaHacks_keeps_it(string game)
    {
        string folder = Env.Copy(game);
        if (folder == null) return;
        var kind = Env.Kind(game);
        byte[] gog = Env.Read(folder, ExeIdentity.ExeName(kind));
        var hack = Hacks.For(kind);
        Assert.False(hack.IsAppliedTo(gog));
        byte[] hacked = hack.Apply(gog);
        Assert.True(hack.IsAppliedTo(hacked));
        // taken as the original, it is used as it is, with or without the box ticked
        Assert.Equal(hacked, Hacks.BuildExe(hacked, kind, true, null));
        Assert.Equal(hacked, Hacks.BuildExe(hacked, kind, false, null));
        // and a mod may not patch the hack's code
        int s = hack.WrittenRanges()[0].Key;
        var pkg = new ModPackage { Manifest = new ModManifest { Id = "t.exe", Name = "Exe", Version = "1", Game = kind == GameKind.UW2 ? "uw2" : "uw1" } };
        var c = new ExeBytesComponent { File = ExeIdentity.ExeName(kind) };
        byte[] old = hacked.Skip(s).Take(1).ToArray();
        var loc = new MzExe(hacked).Locate(s);
        c.Patches.Add(new ExeBytesComponent.Patch { Segment = loc.Segment, Offset = loc.Offset, FileOffset = s, Base = Hashing.Hex(old), Data = Hashing.Hex(new[] { (byte)(old[0] ^ 1) }), Before = "", After = "" });
        pkg.Manifest.Components.Add(c);
        var err = Assert.Throws<ModApplyException>(() => Hacks.BuildExe(hacked, kind, false, pkg));
        Assert.Contains("UltimaHacks", err.Message);
    }

    [Fact]
    public void Library_keys_that_make_the_same_file_name_are_both_kept()
    {
        var lib = new Library(Env.Temp("lib"));
        foreach (var id in new[] { "x-y", "x/y" })
        {
            var pkg = new ModPackage { Manifest = new ModManifest { Id = id, Name = id, Version = "1", Game = "uw1" } };
            pkg.Manifest.Components.Add(new FileComponent { File = "DATA/NEW.DAT", Base = Hashing.Absent, Data = "payload/x" });
            pkg.Files["payload/x"] = new byte[] { 1 };
            lib.Add(pkg);
        }
        Assert.Equal(new[] { "x-y@1", "x/y@1" }, lib.List().Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
