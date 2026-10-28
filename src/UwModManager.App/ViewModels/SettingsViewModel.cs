using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UwModManager.Core;

namespace UwModManager.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    readonly MainViewModel main;
    [ObservableProperty] private string _dosBox;

    public SettingsViewModel(MainViewModel main)
    {
        this.main = main;
        _dosBox = main.Manager.State.Settings.DosBox;
    }

    public string Folder => main.Manager.Paths.Root;
    public string Credits => Hacks.Credit + "\n\nThe file formats and mod packages are the Ultima Underworld Editor's (KarlClinckspoor/UltimaUnderworldEditorUnity). " +
        "The ports are Underworld Exhumed (abedegno/underworld-exhumed); the start square and executable patches need the fork's modding branch.";
    public string HackLicense => Hacks.License();

    partial void OnDosBoxChanged(string value)
    {
        main.Manager.State.Settings.DosBox = value.Trim();
        main.Manager.Save();
    }

    [RelayCommand]
    async Task Browse()
    {
        string? p = await main.Dialogs.PickFile("DOSBox's program", "Programs", OperatingSystem.IsWindows() ? "*.exe" : "*");
        if (p != null) DosBox = p;
    }
}
