namespace UwModManager.Core;

/// <summary>
/// Where the manager keeps its own files: the state (installs, settings), the vault of original
/// files, the library of imported mods, Exhumed's homes for mods and generated DOSBox setups.
/// %APPDATA%\UltimaUnderworldsModManager on Windows, ~/Library/Application Support/... on macOS,
/// $XDG_DATA_HOME (or ~/.local/share)/UltimaUnderworldsModManager elsewhere; UWMM_HOME overrides.
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Vault => Path.Combine(Root, "vault");
    public string Library => Path.Combine(Root, "library");
    public string ExhumedHomes => Path.Combine(Root, "exhumed");
    public string DosBox => Path.Combine(Root, "dosbox");
    public string StateFile => Path.Combine(Root, "state.json");
    public string JournalFile => Path.Combine(Root, "journal.json");
    public string Logs => Path.Combine(Root, "logs");

    public AppPaths(string? root = null)
    {
        Root = root ?? DefaultRoot();
        foreach (var d in new[] { Root, Vault, Library, ExhumedHomes, DosBox, Logs }) Directory.CreateDirectory(d);
    }

    public static string DefaultRoot()
    {
        string? env = Environment.GetEnvironmentVariable("UWMM_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimaUnderworldsModManager");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "UltimaUnderworldsModManager");
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "UltimaUnderworldsModManager");
    }
}
