using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UwModManager.Core;
using Xunit;

namespace UwModManager.Tests;

public class SavesAndLauncherTests
{
    static void MakeSave(string folder, int n, string desc, GameKind kind)
    {
        var g = new GameFolder(folder);
        string dir = Path.Combine(folder, "SAVE" + n);
        Directory.CreateDirectory(dir);
        File.Copy(g.Resolve("DATA/LEV.ARK"), Path.Combine(dir, "LEV.ARK"));
        File.WriteAllBytes(Path.Combine(dir, "BGLOBALS.DAT"), BGlobals.Parse(File.ReadAllBytes(g.Resolve("DATA/BABGLOBS.DAT")), false).ToBytes(true));
        File.WriteAllBytes(Path.Combine(dir, "PLAYER.DAT"), new byte[PlayerDat.RecordSizeFor(kind) + 3]);
        File.WriteAllBytes(Path.Combine(dir, "DESC"), Encoding.ASCII.GetBytes(desc));
        if (kind == GameKind.UW2) File.Copy(g.Resolve("DATA/SCD.ARK"), Path.Combine(dir, "SCD.ARK"));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void A_saved_game_is_exported_installed_backed_up_and_restored(string game)
    {
        string folder = Env.Copy(game);
        if (folder == null) return;
        var kind = Env.Kind(game);
        var m = new Manager(Env.Temp("mgr"));
        var t = m.AddDos(folder);
        MakeSave(folder, 1, "First", kind);
        MakeSave(folder, 2, "Second", kind);
        var slots = Saves.List(m.SavesFolder(t));
        Assert.Equal(4, slots.Count);
        Assert.Equal("First", slots[0].Description);
        Assert.False(slots[2].Exists);
        var pkg = Saves.Export(slots[0], m.GameFiles(t), kind, new ModManifest { Id = "karl.first", Name = "First save", Game = t.Game });
        pkg = ModPackage.FromBytes(pkg.ToBytes());
        Assert.Equal(ModClassifier.SavedGame, pkg.Manifest.Type);
        // into slot 2, over "Second": backed up first
        var problems = Saves.Install(m.Paths, m.Vault, pkg, slots[1], m.GameFiles(t), kind);
        Assert.Empty(problems);
        Assert.Equal("First", Saves.List(folder)[1].Description);
        string backup = Saves.Backups(m.Paths).Single();
        Saves.Restore(m.Paths, m.Vault, backup, Saves.List(folder)[1]);
        Assert.Equal("Second", Saves.List(folder)[1].Description);
        // a saved-game mod is not applied to the game
        m.Library.Add(pkg);
        Assert.Contains(m.Problems(t, pkg.Manifest), p => p.Contains("slot"));
    }

    [Fact]
    public void DosBox_uses_gogs_setups_or_writes_one()
    {
        string folder = Env.Temp("dos");
        Directory.CreateDirectory(Path.Combine(folder, "DATA"));
        File.WriteAllText(Path.Combine(folder, "UW.EXE"), "x");
        string dosbox = Path.Combine(Env.Temp("dosbox"), "dosbox");
        File.WriteAllText(dosbox, "x");
        var m = new Manager(Env.Temp("mgr"));
        m.State.Settings.DosBox = dosbox;
        var t = new Target { Kind = TargetKind.Dos, Folder = folder, Game = "uw1", Name = "UW" };
        var psi = Launcher.DosBox(m.Paths, m.State.Settings, t);
        Assert.Equal(dosbox, psi.FileName);
        string conf = psi.ArgumentList[1];
        Assert.Contains("mount c \"" + folder + "\"", File.ReadAllText(conf));
        Assert.Contains("UW.EXE", File.ReadAllText(conf));
        File.WriteAllText(Path.Combine(folder, "dosboxUW_single.conf"), "[autoexec]\nimgmount d \"..\\game.gog\" -t iso\n");
        File.WriteAllText(Path.Combine(folder, "dosboxUW.conf"), "[sdl]\n");
        psi = Launcher.DosBox(m.Paths, m.State.Settings, t);
        Assert.Equal(new[] { "-conf", Path.Combine(folder, "dosboxUW.conf"), "-conf", Path.Combine(folder, "dosboxUW_single.conf"), "-noconsole", "-c", "exit" }, psi.ArgumentList);
        Assert.Equal(new[] { "dosboxUW_single.conf" }, DosGame.CdImageConfs(folder));
    }

    [Fact]
    public void The_hacks_are_carried_with_their_licence()
    {
        Assert.Contains("MIT License", Hacks.License());
        var h1 = Hacks.For(GameKind.UW1);
        var h2 = Hacks.For(GameKind.UW2);
        Assert.Equal(547248, h1.TargetLength);
        Assert.Equal(675184, h2.TargetLength);
    }
}
