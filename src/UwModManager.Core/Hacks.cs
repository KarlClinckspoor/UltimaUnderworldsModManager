using System.Reflection;
using UltimateEditor.Games;
using UltimateEditor.Modding;
using UltimateEditor.Modding.Exe;

namespace UwModManager.Core;

/// <summary>
/// UltimaHacks (github.com/JohnGlassmyer/UltimaHacks, MIT licence, John Glassmyer): its compiled
/// hacks for the GOG executables, carried by the manager, and the executables built from the
/// originals: original, then the hack if wanted, then the mod's settings and patches.
/// </summary>
public static class Hacks
{
    public const string Credit = "UltimaHacks by John Glassmyer (MIT licence): mouse-look and more for the DOS games; github.com/JohnGlassmyer/UltimaHacks";

    public static UltimaHack For(GameKind game)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(game == GameKind.UW2 ? "hacks/uw2.hack" : "hacks/uw1.hack")
            ?? throw new FileNotFoundException("the UltimaHacks hack is missing from the program");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return UltimaHack.Parse(ms.ToArray());
    }

    public static string License()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("hacks/LICENSE-UltimaHacks.txt");
        return s == null ? "" : new StreamReader(s).ReadToEnd();
    }

    /// <summary>
    /// The executable to run: <paramref name="original"/> (which must be the GOG release's when
    /// UltimaHacks is wanted), with UltimaHacks' edits, then the mod's executable changes (they
    /// follow an overlay UltimaHacks moved, and may not touch what the hack wrote).
    /// </summary>
    public static byte[] BuildExe(byte[] original, GameKind game, bool ultimaHacks, ModPackage? mod)
    {
        byte[] exe = (byte[])original.Clone();
        IList<KeyValuePair<int, int>>? written = null;
        if (ultimaHacks)
        {
            if (!ExeIdentity.IsGogOriginal(original, game))
                throw new ModApplyException(ExeIdentity.ExeName(game) + " is not the GOG release's, which UltimaHacks' hack is made for");
            var hack = For(game);
            exe = hack.Apply(original);
            written = hack.WrittenRanges();
        }
        if (mod != null && ModApply.ChangesExe(mod.Manifest)) ModApply.ApplyExe(mod, exe, written);
        return exe;
    }
}
