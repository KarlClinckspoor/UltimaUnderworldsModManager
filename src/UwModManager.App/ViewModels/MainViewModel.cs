using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UwModManager.Core;

namespace UwModManager.App.ViewModels;

/// <summary>An entry of the list on the left: a game, the library or the settings.</summary>
public sealed partial class NavItem : ObservableObject
{
    public required string Title { get; init; }
    [ObservableProperty] private string _subtitle = "";
    public required Func<ViewModelBase> Page { get; init; }
    public Target? Target { get; init; }
}

public sealed partial class MainViewModel : ViewModelBase
{
    public Manager Manager { get; }
    public IDialogs Dialogs { get; }
    public ObservableCollection<NavItem> Games { get; } = new();
    public ObservableCollection<NavItem> Other { get; } = new();

    [ObservableProperty] private NavItem? _selectedGame;
    [ObservableProperty] private NavItem? _selectedOther;
    [ObservableProperty] private ViewModelBase _page = new WelcomeViewModel();
    [ObservableProperty] private string _status = "";

    public MainViewModel(Manager manager, IDialogs dialogs)
    {
        Manager = manager;
        Dialogs = dialogs;
        try
        {
            string? r = manager.Recover();
            if (r != null) Status = r;
        }
        catch (Exception e) { Status = "An interrupted change could not be finished: " + e.Message; }
        Other.Add(new NavItem { Title = "Library", Subtitle = "Imported mods", Page = () => new LibraryViewModel(this) });
        Other.Add(new NavItem { Title = "Settings", Subtitle = "DOSBox, credits", Page = () => new SettingsViewModel(this) });
        RefreshGames();
        if (Games.Count > 0) SelectedGame = Games[0];
    }

    public void RefreshGames(Target? select = null)
    {
        Games.Clear();
        foreach (var t in Manager.State.Targets)
        {
            var item = new NavItem { Title = t.Name, Target = t, Page = () => new TargetViewModel(this, t) };
            UpdateSubtitle(item);
            Games.Add(item);
        }
        if (select != null) SelectedGame = Games.FirstOrDefault(g => g.Target == select);
    }

    public void UpdateSubtitle(NavItem item)
    {
        var t = item.Target!;
        string mod = t.ActiveMod == null ? "original" : Manager.Library.Find(t.ActiveMod)?.Manifest.Name ?? t.ActiveMod;
        item.Subtitle = (t.Kind == TargetKind.Dos ? "DOS" : "Exhumed") + " · " + mod + (t.UltimaHacks ? " · UltimaHacks" : "");
    }

    public void UpdateSubtitles() { foreach (var g in Games) UpdateSubtitle(g); }

    partial void OnSelectedGameChanged(NavItem? value)
    {
        if (value == null) return;
        SelectedOther = null;
        Page = value.Page();
    }

    partial void OnSelectedOtherChanged(NavItem? value)
    {
        if (value == null) return;
        SelectedGame = null;
        Page = value.Page();
    }

    [RelayCommand]
    async Task AddDos()
    {
        string? folder = await Dialogs.PickFolder("The DOS game's folder (UW.EXE or UW2.EXE and DATA)");
        if (folder == null) return;
        await Guard(async () =>
        {
            var t = Manager.AddDos(folder);
            var (gog, differ, missing) = Manager.CompareWithGog(t);
            var notes = new List<string> { $"{t.Baseline.Count} files are kept in the vault: the manager can always put the game back as it is now." };
            notes.Add(gog ? "These are the GOG release's files." : $"These are not quite the GOG release's files ({differ.Count} differ, {missing.Count} missing); mods made from the GOG release may not install.");
            foreach (var c in DosGame.CdImageConfs(t.Folder))
                notes.Add($"{c} runs the game from GOG's CD image (game.gog), which never sees files changed in the folder. Extract the image into the folder (7-Zip opens it) and change that setup to mount the folder instead.");
            RefreshGames(t);
            await Dialogs.Tell("Added " + t.Name, string.Join("\n\n", notes));
        });
    }

    [RelayCommand]
    async Task AddExhumed()
    {
        string? exe = await Dialogs.PickFile("Underworld Exhumed's program (uw1port or uw2port)", "Programs", OperatingSystem.IsWindows() ? "*.exe" : "*");
        if (exe == null) return;
        string? folder = null;
        int which = await Dialogs.Ask("The game folder", "Exhumed reads your copy of the game. Use the folder its own settings remember, or choose one?", "The remembered one", "Choose...");
        if (which < 0) return;
        if (which == 1) { folder = await Dialogs.PickFolder("The game folder Exhumed reads"); if (folder == null) return; }
        await Guard(async () =>
        {
            var t = Manager.AddExhumed(exe, folder);
            RefreshGames(t);
            await Dialogs.Tell("Added " + t.Name, t.Features == null
                ? "This build does not report mod features (--features). Mods of the game's data files play in it; a start square or patches of the executable need a build of the fork's modding branch."
                : $"Start square: {(t.Features.StartSquare ? "yes" : "no")}. Executable data patches: {(t.Features.ExePatch ? "yes" : "no")}. Enhancements: {string.Join(", ", t.Features.Enhancements)}.");
        });
    }

    /// <summary>Runs an action, turning failures into a message.</summary>
    public async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e)
        {
            Status = e.Message;
            await Dialogs.Tell("That did not work", e.Message);
        }
    }
}
