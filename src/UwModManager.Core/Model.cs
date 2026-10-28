using System.Text.Json;
using System.Text.Json.Serialization;

namespace UwModManager.Core;

public enum TargetKind
{
    /// <summary>The DOS game in its folder, run in DOSBox; mods are written in place, originals kept in the vault.</summary>
    Dos,
    /// <summary>Underworld Exhumed's port (uw1port / uw2port); mods go into a home folder of their own.</summary>
    Exhumed,
}

/// <summary>A game the manager installs mods into.</summary>
public sealed class Target
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>"uw1" or "uw2".</summary>
    public string Game { get; set; } = "uw1";
    public TargetKind Kind { get; set; }
    /// <summary>DOS: the game folder (UW.EXE / UW2.EXE, DATA...). Exhumed: the game folder it reads.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Exhumed: the port's program.</summary>
    public string? PortExe { get; set; }

    /// <summary>The mod in use ("id@version"), or null for the original game. One at a time.</summary>
    public string? ActiveMod { get; set; }
    /// <summary>DOS: run with UltimaHacks' changes to the executable (built from the original).</summary>
    public bool UltimaHacks { get; set; }
    /// <summary>Exhumed: the enhancements to start with (on top of a mod's recommendation, if taken).</summary>
    public List<string> Enhancements { get; set; } = new();

    /// <summary>DOS: the hash of every game file when the manager took the folder over (the originals, kept in the vault).</summary>
    public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Files the manager wrote (DOS: in the folder; Exhumed: in the mod's home), by hash.</summary>
    public Dictionary<string, string> Written { get; set; } = new(StringComparer.Ordinal);
    /// <summary>DOS: files that are not originals (added after the take-over) which a mod overwrote,
    /// by the hash of what was there (kept in the vault): the original game puts them back.</summary>
    public Dictionary<string, string> Displaced { get; set; } = new(StringComparer.Ordinal);
    /// <summary>DOS: changes made outside the manager that it overwrote when told to (kept in the vault).</summary>
    public List<OverwrittenFile> Overwritten { get; set; } = new();
    /// <summary>DOS: the stamp of the last change the manager finished (matched against an interrupted journal).</summary>
    public string? LastChange { get; set; }
    /// <summary>Exhumed: the home folder Written refers to.</summary>
    public string? WrittenHome { get; set; }
    /// <summary>Exhumed: what --features reported (null: not asked yet, or an old build without it).</summary>
    public PortFeatures? Features { get; set; }

    [JsonIgnore] public UltimateEditor.Games.GameKind GameKind => Game == "uw2" ? UltimateEditor.Games.GameKind.UW2 : UltimateEditor.Games.GameKind.UW1;
    [JsonIgnore] public string ExeName => Game == "uw2" ? "UW2.EXE" : "UW.EXE";
}

public sealed class OverwrittenFile
{
    public string File { get; set; } = "";
    public string Sha { get; set; } = "";
    public DateTime When { get; set; }
}

/// <summary>What an Exhumed build says it can do (docs/MODDING.md of the fork, --features).</summary>
public sealed class PortFeatures
{
    public bool StartSquare { get; set; }
    public bool DataOverlay { get; set; }
    /// <summary>The executable's file ranges --exe-patch can change, [start, end).</summary>
    public List<long[]> ExePatchRanges { get; set; } = new();
    public List<string> Enhancements { get; set; } = new();
    public bool ExePatch => ExePatchRanges.Count > 0;
}

public sealed class Settings
{
    /// <summary>DOSBox's program; empty: GOG's in the game folder, else dosbox on the PATH.</summary>
    public string DosBox { get; set; } = "";
}

public sealed class ManagerState
{
    public int Format { get; set; } = 1;
    public List<Target> Targets { get; set; } = new();
    public Settings Settings { get; set; } = new();

    public Target? Find(string idOrName) =>
        Targets.FirstOrDefault(t => t.Id == idOrName) ?? Targets.FirstOrDefault(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static ManagerState Load(string path)
    {
        if (!File.Exists(path)) return new ManagerState();
        return JsonSerializer.Deserialize<ManagerState>(File.ReadAllText(path), Json) ?? new ManagerState();
    }

    public void Save(string path) => Files.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)));
}
