using System.Text;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UltimateEditor.Modding.Exe;
using UwModManager.Core;
using Xunit;

namespace UwModManager.Tests;

/// <summary>DOS folders changed in place: take over, use a mod, switch, back to the original, UltimaHacks, outside changes, the journal.</summary>
public class DosTests
{
    static (Manager m, Target t, string folder) Setup(string game)
    {
        string folder = Env.Copy(game);
        if (folder == null) return (null, null, null);
        var m = new Manager(Env.Temp("mgr"));
        return (m, m.AddDos(folder), folder);
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void Taking_over_keeps_every_original_and_knows_the_gog_release(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        Assert.Equal(game == "UW2" ? "uw2" : "uw1", t.Game);
        Assert.True(t.Baseline.Count > 250);
        Assert.All(t.Baseline.Values, sha => Assert.True(m.Vault.Has(sha)));
        var (gog, differ, missing) = m.CompareWithGog(t);
        Assert.True(gog, "differ: " + string.Join(", ", differ) + "; missing: " + string.Join(", ", missing));
        Assert.Empty(DosGame.LocalChanges(t));
        // and a second manager reads the same state back
        var again = new Manager(m.Paths.Root);
        Assert.Equal(t.Baseline, again.Get(t.Id).Baseline);
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void A_mod_is_applied_switched_and_removed_exactly(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        var kind = Env.Kind(game);
        string modded = null;
        var a = Env.MakeMod(game, f => { Env.EditLevel(f, kind, 1, 100, 1); Env.EditString(f, 4, 3, "a gleaming sword"); modded = f; }, "test.a");
        var b = Env.MakeMod(game, f => Env.EditLevel(f, kind, 2, 200, 4), "test.b");
        m.Library.Add(a); m.Library.Add(b);
        string ka = Library.KeyOf(a.Manifest), kb = Library.KeyOf(b.Manifest);
        Assert.Empty(m.Problems(t, a.Manifest));

        var plan = m.UseDos(t, ka, false);
        Assert.Equal(2, plan.Writes.Count);
        Assert.Equal(ka, t.ActiveMod);
        foreach (var rel in new[] { "DATA/LEV.ARK", "DATA/STRINGS.PAK" })
            Assert.Equal(Env.Read(modded, rel), Env.Read(folder, rel));
        Assert.Empty(DosGame.LocalChanges(t));

        // the second mod: STRINGS.PAK goes back, LEV.ARK changes
        plan = m.UseDos(t, kb, false);
        Assert.Equal(new[] { "DATA/LEV.ARK", "DATA/STRINGS.PAK" }, plan.Writes.Keys);
        Assert.Equal(DosGame.Original(t, m.Vault, "DATA/STRINGS.PAK"), Env.Read(folder, "DATA/STRINGS.PAK"));
        Assert.Single(t.Written);

        // the original game: every file as it was
        m.UseDos(t, null, false);
        Assert.Empty(t.Written);
        Assert.Null(t.ActiveMod);
        foreach (var kv in t.Baseline) Assert.Equal(kv.Value, Files.Sha(Env.Read(folder, kv.Key)));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void UltimaHacks_and_a_start_square_go_together(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        var kind = Env.Kind(game);
        var mod = Env.MakeMod(game, f => Env.SetStart(f, kind, 33, 5), "test.start");
        m.Library.Add(mod);
        byte[] orig = DosGame.Original(t, m.Vault, t.ExeName);
        m.UseDos(t, null, true);
        byte[] hacked = Env.Read(folder, t.ExeName);
        Assert.Equal(Hacks.For(kind).Apply(orig), hacked);
        Assert.Equal(orig.Length + (kind == GameKind.UW2 ? 9472 : 8704), hacked.Length);
        m.UseDos(t, Library.KeyOf(mod.Manifest), true);
        byte[] both = Env.Read(folder, t.ExeName);
        Assert.Equal(hacked.Length, both.Length);
        Assert.True(StartPosition.Read(both, kind, out int x, out int y));
        Assert.Equal((33, 5), (x, y));
        // without the hack: the original with the start square
        m.UseDos(t, Library.KeyOf(mod.Manifest), false);
        byte[] plain = Env.Read(folder, t.ExeName);
        Assert.Equal(orig.Length, plain.Length);
        Assert.True(StartPosition.Read(plain, kind, out x, out y));
        Assert.Equal((33, 5), (x, y));
        m.UseDos(t, null, false);
        Assert.Equal(orig, Env.Read(folder, t.ExeName));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void A_patch_in_the_overlay_UltimaHacks_moves_follows_it(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        var kind = Env.Kind(game);
        byte[] orig = DosGame.Original(t, m.Vault, t.ExeName);
        var hack = Hacks.For(kind);
        var mz = new MzExe(orig);
        // the overlay the hack grows: the one whose code it copies to the end of the file
        var copy = hack.Edits.First(e => e.Kind == UltimaHack.EditKind.Copy);
        var ov = mz.Segments.First(s => s.IsOverlay && s.OverlayStart == copy.Source);
        var written = hack.WrittenRanges();
        var hacked = hack.Apply(orig);
        var relocs = mz.RelocatedWords();
        int at = ov.OverlayStart + 40;
        int moved(int a) => copy.Destination + (a - ov.OverlayStart);
        while (mz.TouchesRelocation(at, 1, relocs) || written.Any(r => moved(at) >= r.Key && moved(at) < r.Value)) at++;
        var mod = Env.MakeMod(game, f =>
        {
            string p = new GameFolder(f).Resolve(t.ExeName);
            byte[] e = File.ReadAllBytes(p); e[at] ^= 0x20; File.WriteAllBytes(p, e);
        }, "test.ovpatch");
        m.Library.Add(mod);
        var patch = mod.Manifest.Components.OfType<ExeBytesComponent>().Single().Patches.Single();
        Assert.Equal(ov.Index, patch.Segment);
        m.UseDos(t, Library.KeyOf(mod.Manifest), true);
        byte[] exe = Env.Read(folder, t.ExeName);
        Assert.Equal((byte)(orig[at] ^ 0x20), exe[moved(at)]);
        Assert.Equal(hacked[at], exe[at]);   // the old copy, dead, as the hack left it
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void Outside_changes_are_never_overwritten_without_asking(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        var kind = Env.Kind(game);
        var a = Env.MakeMod(game, f => Env.EditLevel(f, kind, 1, 300, 2), "test.a");
        m.Library.Add(a);
        Env.EditLevel(folder, kind, 1, 500, 8);   // the editor saved a change
        Assert.Equal(new[] { "DATA/LEV.ARK" }, DosGame.LocalChanges(t));
        var plan = m.PlanDos(t, Library.KeyOf(a.Manifest), false);
        Assert.Equal(new[] { "DATA/LEV.ARK" }, plan.OutsideChanges);
        Assert.Throws<ModApplyException>(() => m.UseDos(t, Library.KeyOf(a.Manifest), false));
        Assert.Null(t.ActiveMod);
        // kept as a mod, then overwritten
        var (cap, diff) = m.Capture(t, new ModManifest { Id = "karl.local", Name = "Local changes" });
        Assert.NotNull(cap);
        Assert.Single(cap.Manifest.Components);
        m.UseDos(t, Library.KeyOf(a.Manifest), false, overwrite: true);
        Assert.Empty(DosGame.LocalChanges(t));
        // the captured mod gives the editor's change back on the originals
        m.Library.Add(cap);
        m.UseDos(t, Library.KeyOf(cap.Manifest), false);
        var lev = Archive.Load(new GameFolder(folder).Resolve("DATA/LEV.ARK"), kind);
        var orig = Archive.Open(DosGame.Original(t, m.Vault, "DATA/LEV.ARK"), kind);
        int blk = GameProfile.For(kind).LevelBlock(1);
        Assert.Equal((byte)(orig.Read(blk)[500] ^ 8), lev.Read(blk)[500]);
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void An_interrupted_change_is_finished_at_the_next_start(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        var kind = Env.Kind(game);
        var a = Env.MakeMod(game, f => { Env.EditLevel(f, kind, 1, 100, 1); Env.EditString(f, 4, 3, "x"); }, "test.a");
        m.Library.Add(a);
        string key = Library.KeyOf(a.Manifest);
        // a crash after the journal and one file: write the journal and LEV.ARK by hand
        var plan = m.PlanDos(t, key, false);
        File.WriteAllText(m.Paths.JournalFile, "{\"target\":\"" + t.Id + "\",\"mod\":\"" + key + "\",\"ultimahacks\":false}");
        File.WriteAllBytes(new GameFolder(folder).Resolve("DATA/LEV.ARK"), plan.Writes["DATA/LEV.ARK"]);
        var m2 = new Manager(m.Paths.Root);
        Assert.Contains("interrupted", m2.Recover());
        var t2 = m2.Get(t.Id);
        Assert.Equal(key, t2.ActiveMod);
        Assert.Equal(plan.Writes["DATA/STRINGS.PAK"], Env.Read(folder, "DATA/STRINGS.PAK"));
        Assert.False(File.Exists(m.Paths.JournalFile));
        Assert.Empty(DosGame.LocalChanges(t2));
    }

    [Theory, MemberData(nameof(Env.Games), MemberType = typeof(Env))]
    public void A_mod_for_other_files_or_the_other_game_is_refused(string game)
    {
        var (m, t, folder) = Setup(game);
        if (m == null) return;
        string other = game == "UW" ? "UW2" : "UW";
        if (Directory.Exists(Path.Combine(Env.Root, other, "DATA")))
        {
            var o = Env.MakeMod(other, f => Env.EditString(f, 4, 1, "x"), "test.other");
            Assert.Contains(m.Problems(t, o.Manifest), p => p.Contains("it is for"));
        }
        // made from another STRINGS.PAK: a note; changes to parts that are the same still apply
        var kind = Env.Kind(game);
        string changedBase = Env.Copy(game), modded = Env.Copy(game);
        Env.EditString(changedBase, 4, 1, "changed by someone");
        Env.EditString(modded, 4, 1, "changed by someone");
        Env.EditString(modded, 4, 2, "and by the mod");
        var pkg = Package(game, changedBase, modded, "test.base");
        m.Library.Add(pkg);
        Assert.Empty(m.Problems(t, pkg.Manifest));
        Assert.Contains(m.Notes(t, pkg.Manifest), p => p.Contains("STRINGS.PAK"));
        m.UseDos(t, Library.KeyOf(pkg.Manifest), false);
        var s = StringsPakFile.Parse(Env.Read(folder, "DATA/STRINGS.PAK"));
        Assert.Equal("and by the mod", s.Find(4).Strings[2]);
        Assert.NotEqual("changed by someone", s.Find(4).Strings[1]);
        m.UseDos(t, null, false);
        // a change to a part that is not the same: refused before anything is written
        string modded2 = Env.Copy(game);
        Env.EditString(modded2, 4, 1, "changed by someone, then the mod");
        var pkg2 = Package(game, changedBase, modded2, "test.base2");
        m.Library.Add(pkg2);
        Assert.Throws<ModApplyException>(() => m.UseDos(t, Library.KeyOf(pkg2.Manifest), false));
        Assert.Empty(t.Written);
    }

    static ModPackage Package(string game, string orig, string mod, string id)
    {
        var diff = ModDiff.Compare(new GameFolder(orig), new GameFolder(mod), Env.Kind(game));
        return ModBuilder.FromDiff(diff, new ModManifest { Id = id, Name = id, Game = game == "UW2" ? "uw2" : "uw1" }, new GameFolder(orig));
    }
}
