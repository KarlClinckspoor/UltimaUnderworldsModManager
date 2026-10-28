using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateEditor.Modding;
using UwModManager.Core;

namespace UwModManager.App.ViewModels;

/// <summary>A choice in a game's mod list: a library mod, or the original game.</summary>
public sealed class ModChoice
{
    public string? Key { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Problems { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Changes { get; init; } = new();
    public bool Usable => Problems.Count == 0;
    public bool InUse { get; init; }
    public string Badge => InUse ? "in use" : Usable ? "" : "not usable here";
}

public sealed partial class EnhancementChoice : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private bool _on;
}

public sealed class SlotItem
{
    public required SaveSlot Slot { get; init; }
    public string Title => "SAVE" + Slot.Number;
    public string Text => Slot.Exists ? (Slot.Description.Length > 0 ? Slot.Description : "(no description)") + "   " + Slot.Written.ToString("yyyy-MM-dd HH:mm") : "empty";
    public bool Exists => Slot.Exists;
}

/// <summary>A game: its mod (one at a time), UltimaHacks or Exhumed's enhancements, its saves, and Play.</summary>
public sealed partial class TargetViewModel : ViewModelBase
{
    readonly MainViewModel main;
    public Target Target { get; }
    Manager M => main.Manager;

    public ObservableCollection<ModChoice> Choices { get; } = new();
    public ObservableCollection<EnhancementChoice> Enhancements { get; } = new();
    public ObservableCollection<SlotItem> Slots { get; } = new();
    public ObservableCollection<string> StatusLines { get; } = new();

    [ObservableProperty] private ModChoice? _selected;
    [ObservableProperty] private bool _ultimaHacks;
    [ObservableProperty] private bool _useRecommended = true;
    [ObservableProperty] private string _localChanges = "";
    [ObservableProperty] private bool _busy;

    public bool IsDos => Target.Kind == TargetKind.Dos;
    public bool IsExhumed => Target.Kind == TargetKind.Exhumed;
    public string Header => Target.Name;
    public string Kind => (IsDos ? "DOS game, changed in place" : "Underworld Exhumed, a home folder per mod") + " · " + (Target.Game == "uw2" ? "Ultima Underworld II" : "Ultima Underworld");
    public string Folder => Target.Folder + (IsExhumed ? "\n" + Target.PortExe : "");
    public bool HasLocalChanges => LocalChanges.Length > 0;
    public string UltimaHacksCredit => Hacks.Credit;

    public TargetViewModel(MainViewModel main, Target target)
    {
        this.main = main;
        Target = target;
        _ultimaHacks = target.UltimaHacks;
        Refresh();
    }

    public void Refresh()
    {
        Choices.Clear();
        Choices.Add(new ModChoice { Key = null, Title = "The original game", Detail = "no mod", InUse = Target.ActiveMod == null,
            Description = IsDos ? "Every file as it was when the manager took the folder over." : "The game as Exhumed plays it, in a home folder of its own." });
        foreach (var e in M.Library.List().Where(e => e.Manifest.Game == Target.Game))
        {
            var mf = e.Manifest;
            Choices.Add(new ModChoice
            {
                Key = e.Key, Title = mf.Name + "  " + mf.Version,
                Detail = mf.Type + (mf.Authors.Count > 0 ? " · by " + string.Join(", ", mf.Authors) : ""),
                Description = mf.Description, InUse = Target.ActiveMod == e.Key,
                Problems = M.Problems(Target, mf), Notes = M.Notes(Target, mf),
                Warnings = ModClassifier.Warnings(mf), Changes = mf.Components.Select(c => c.Summary).ToList(),
            });
        }
        Selected = Choices.FirstOrDefault(c => c.InUse) ?? Choices[0];

        Enhancements.Clear();
        if (IsExhumed && Target.Features != null)
            foreach (var n in Target.Features.Enhancements)
            {
                var ec = new EnhancementChoice { Name = n, On = Target.Enhancements.Contains(n) };
                ec.PropertyChanged += (_, _) => SaveEnhancements();
                Enhancements.Add(ec);
            }

        StatusLines.Clear();
        if (IsDos)
        {
            var (gog, differ, _) = M.CompareWithGog(Target);
            StatusLines.Add(gog ? "Originals: the GOG release." : $"Originals: {differ.Count} file(s) differ from the GOG release.");
            var local = DosGame.LocalChanges(Target);
            LocalChanges = local.Count == 0 ? "" : "Changed outside the manager: " + string.Join(", ", local.Take(8)) + (local.Count > 8 ? $" and {local.Count - 8} more" : "") +
                ". Capture them as a mod before switching, or they will be asked about.";
            foreach (var c in DosGame.CdImageConfs(Target.Folder)) StatusLines.Add($"{c} runs the game from GOG's CD image (game.gog), which never sees the folder's files.");
        }
        else
        {
            StatusLines.Add(Target.Features == null ? "This Exhumed build reports no mod features: only mods of data files play in it."
                : $"Start square: {(Target.Features.StartSquare ? "yes" : "no")} · executable data patches: {(Target.Features.ExePatch ? "yes" : "no")}");
            StatusLines.Add("Home: " + ExhumedGame.HomeFor(M.Paths, Target, Target.ActiveMod));
        }
        OnPropertyChanged(nameof(HasLocalChanges));

        Slots.Clear();
        foreach (var s in Saves.List(M.SavesFolder(Target))) Slots.Add(new SlotItem { Slot = s });
    }

    void SaveEnhancements()
    {
        Target.Enhancements = Enhancements.Where(e => e.On).Select(e => e.Name).ToList();
        M.Save();
    }

    [RelayCommand]
    async Task Apply()
    {
        var choice = Selected;
        if (choice == null) return;
        if (!choice.Usable) { await main.Dialogs.Tell("This mod cannot be used here", string.Join("\n", choice.Problems)); return; }
        await main.Guard(async () =>
        {
            Busy = true;
            try
            {
                if (IsExhumed)
                {
                    string home = await Task.Run(() => M.UseExhumed(Target, choice.Key));
                    main.Status = $"{Target.Name}: {choice.Title} (home {home}).";
                }
                else
                {
                    var plan = await Task.Run(() => M.PlanDos(Target, choice.Key, UltimaHacks));
                    bool overwrite = false;
                    if (plan.OutsideChanges.Count > 0)
                    {
                        int a = await main.Dialogs.Ask("Files changed outside the manager",
                            "These files changed since the manager last wrote them (the editor saved them, perhaps):\n\n" + string.Join("\n", plan.OutsideChanges) +
                            "\n\nSwitching overwrites them. Keep them as a mod first?", "Capture them first", "Overwrite them", "Cancel");
                        if (a == 0) { await Capture(); return; }
                        if (a != 1) return;
                        overwrite = true;
                    }
                    await Task.Run(() => M.UseDos(Target, choice.Key, UltimaHacks, overwrite));
                    main.Status = $"{Target.Name}: {choice.Title}{(UltimaHacks ? " with UltimaHacks" : "")}, {plan.Writes.Count} file(s) written.";
                    if (plan.Warnings.Count > 0) await main.Dialogs.Tell(choice.Title, string.Join("\n\n", plan.Warnings));
                }
            }
            finally { Busy = false; }
            main.UpdateSubtitles();
            Refresh();
        });
    }

    [RelayCommand]
    async Task Play()
    {
        await main.Guard(async () =>
        {
            if (IsDos && (Target.ActiveMod != (Selected?.Key) || Target.UltimaHacks != UltimaHacks))
            {
                int a = await main.Dialogs.Ask("Not applied yet", "The game still has " + (Target.ActiveMod ?? "the original game") + ". Apply the choice first?", "Apply, then play", "Play as it is", "Cancel");
                if (a == 2 || a < 0) return;
                if (a == 0) { await Apply(); }
            }
            var psi = M.LaunchInfo(Target, UseRecommended);
            System.Diagnostics.Process.Start(psi);
            main.Status = "Started: " + Launcher.CommandLine(psi);
            Refresh();
        });
    }

    [RelayCommand]
    async Task Capture()
    {
        var d = await main.Dialogs.AskDetails("Keep this folder's changes as a mod", new ModManifest { Name = "My changes", Version = "1.0.0" });
        if (d == null) return;
        await main.Guard(async () =>
        {
            var (pkg, diff) = await Task.Run(() => M.Capture(Target, d));
            if (pkg == null)
            {
                await main.Dialogs.Tell("Nothing captured", diff.Components.Count == 0 && diff.Problems.Count == 0 ? "Nothing changed." : string.Join("\n", diff.Problems));
                return;
            }
            M.Library.Add(pkg);
            main.Status = $"Captured {pkg.Manifest.Components.Count} change(s) as {pkg.Manifest.Name} {pkg.Manifest.Version} ({pkg.Manifest.Type}), now in the library.";
            Refresh();
        });
    }

    [RelayCommand]
    async Task ExportSave(SlotItem? slot)
    {
        if (slot == null || !slot.Exists) return;
        var d = await main.Dialogs.AskDetails("Export SAVE" + slot.Slot.Number + " as a mod", new ModManifest { Name = slot.Slot.Description.Length > 0 ? slot.Slot.Description : "Saved game", Version = "1.0.0" });
        if (d == null) return;
        await main.Guard(async () =>
        {
            d.Game = Target.Game;
            var pkg = Saves.Export(slot.Slot, M.GameFiles(Target), Target.GameKind, d);
            string? path = await main.Dialogs.SaveFile("Save the package", pkg.Manifest.Id + "-" + pkg.Manifest.Version + ModPackage.Extension, "uwmod");
            if (path == null) { M.Library.Add(pkg); main.Status = "Exported into the library."; }
            else { pkg.Save(path); main.Status = "Exported to " + path; }
        });
    }

    [RelayCommand]
    async Task InstallSave(SlotItem? slot)
    {
        if (slot == null) return;
        var saves = M.Library.List().Where(e => e.Manifest.Game == Target.Game && e.Manifest.Type == ModClassifier.SavedGame).ToList();
        if (saves.Count == 0) { await main.Dialogs.Tell("No saved games in the library", "Import a saved-game mod into the Library first."); return; }
        int which = await main.Dialogs.Ask("Install a saved game into SAVE" + slot.Slot.Number, (slot.Exists ? "The slot's game is backed up first (Restore a backup brings it back).\n\n" : "") + "Which one?", saves.Select(s => s.Manifest.Name + " " + s.Manifest.Version).ToArray());
        if (which < 0) return;
        await main.Guard(async () =>
        {
            var pkg = M.Library.Load(saves[which].Key);
            var problems = Saves.Install(M.Paths, M.Vault, pkg, slot.Slot, M.GameFiles(Target), Target.GameKind);
            main.Status = "Installed " + pkg.Manifest.Name + " into SAVE" + slot.Slot.Number + ".";
            if (problems.Count > 0) await main.Dialogs.Tell("Installed, with warnings", string.Join("\n", problems));
            Refresh();
        });
    }

    [RelayCommand]
    async Task RestoreSave(SlotItem? slot)
    {
        if (slot == null) return;
        var backups = Saves.Backups(M.Paths).Where(b => b.EndsWith("-SAVE" + slot.Slot.Number + ".json")).Take(8).ToList();
        if (backups.Count == 0) { await main.Dialogs.Tell("No backups", "No backup of SAVE" + slot.Slot.Number + " yet: one is made whenever the manager replaces a slot."); return; }
        int which = await main.Dialogs.Ask("Restore SAVE" + slot.Slot.Number, "The slot's game now is backed up first. Which backup?", backups.Select(Path.GetFileNameWithoutExtension).ToArray()!);
        if (which < 0) return;
        await main.Guard(async () =>
        {
            Saves.Restore(M.Paths, M.Vault, backups[which], slot.Slot);
            main.Status = "SAVE" + slot.Slot.Number + " restored.";
            Refresh();
            await Task.CompletedTask;
        });
    }

    [RelayCommand]
    async Task Remove()
    {
        int a = await main.Dialogs.Ask("Forget " + Target.Name, IsDos && Target.Written.Count > 0
            ? "The folder has a mod in place. Put the original game back first?" : "The manager forgets this game; its files stay as they are.", IsDos && Target.Written.Count > 0 ? new[] { "Put it back, then forget", "Just forget", "Cancel" } : new[] { "Forget", "Cancel" });
        bool restore = IsDos && Target.Written.Count > 0 && a == 0;
        bool forget = IsDos && Target.Written.Count > 0 ? a is 0 or 1 : a == 0;
        if (!forget) return;
        await main.Guard(async () =>
        {
            if (restore) await Task.Run(() => M.UseDos(Target, null, false));
            M.Remove(Target);
            main.RefreshGames();
            main.Page = new WelcomeViewModel();
        });
    }

    [RelayCommand]
    void Reload() => Refresh();
}
