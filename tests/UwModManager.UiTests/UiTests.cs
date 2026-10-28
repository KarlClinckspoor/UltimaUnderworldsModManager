using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using UltimateEditor.Modding;
using UwModManager.App.ViewModels;
using UwModManager.App.Views;
using UwModManager.Core;
using UwModManager.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(UwModManager.UiTests.TestApp))]

namespace UwModManager.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UwModManager.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Answers the questions the way a test wants, and remembers what it was told.</summary>
public sealed class FakeDialogs : IDialogs
{
    public readonly List<string> Told = new();
    public Queue<int> Answers = new();
    public Queue<string> Paths = new();
    public ModManifest Details;
    public Task<string> PickFolder(string title) => Task.FromResult(Paths.Count > 0 ? Paths.Dequeue() : null);
    public Task<string> PickFile(string title, string n, params string[] p) => Task.FromResult(Paths.Count > 0 ? Paths.Dequeue() : null);
    public Task<string> SaveFile(string title, string s, string e) => Task.FromResult(Paths.Count > 0 ? Paths.Dequeue() : null);
    public Task<int> Ask(string title, string text, params string[] buttons) { Told.Add(title + ": " + text); return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : 0); }
    public Task Tell(string title, string text) { Told.Add(title + ": " + text); return Task.CompletedTask; }
    public Task<ModManifest> AskDetails(string title, ModManifest d) => Task.FromResult(Details);
}

public class UiTests
{
    static string Shots => Path.Combine(AppContext.BaseDirectory, "screenshots");

    static void Shot(Window w, string name)
    {
        Directory.CreateDirectory(Shots);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(Shots, name + ".png"));
    }

    [AvaloniaFact]
    public async Task The_screens_render_and_a_mod_is_used_through_the_view_models()
    {
        string folder = Env.Copy("UW");
        if (folder == null) return;
        var kind = Env.Kind("UW");
        var m = new Manager(Env.Temp("ui"));
        var lvl = Env.MakeMod("UW", f => { Env.EditLevel(f, kind, 1, 100, 1); Env.SetStart(f, kind, 33, 5); }, "karl.gate-of-dawn", "1.2.0");
        lvl.Manifest.Name = "Gate of Dawn"; lvl.Manifest.Description = "Rebuilds the first level around a new entrance and moves the start next to it.";
        lvl.Manifest.ExhumedRecommended.Add("tidy-objects");
        var txt = Env.MakeMod("UW", f => Env.EditString(f, 4, 3, "a gleaming sword"), "karl.better-names", "0.3.0");
        txt.Manifest.Name = "Better item names"; txt.Manifest.Description = "Clearer names for weapons and armour.";
        m.Library.Add(lvl); m.Library.Add(txt);
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(m, dialogs);
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 780 };
        w.Show();
        Shot(w, "1-welcome");

        // add the DOS game through the view model, as the button does
        dialogs.Paths.Enqueue(folder);
        await vm.AddDosCommand.ExecuteAsync(null);
        Assert.Single(m.State.Targets);
        Assert.Contains(dialogs.Told, t => t.Contains("GOG release"));
        var page = Assert.IsType<TargetViewModel>(vm.Page);
        Assert.Equal(3, page.Choices.Count);
        Shot(w, "2-game");

        page.Selected = page.Choices.First(c => c.Key == Library.KeyOf(lvl.Manifest));
        page.UltimaHacks = true;
        await page.ApplyCommand.ExecuteAsync(null);
        var t = m.State.Targets[0];
        Assert.Equal(Library.KeyOf(lvl.Manifest), t.ActiveMod);
        Assert.True(t.UltimaHacks);
        Assert.Contains("Gate of Dawn", vm.Games[0].Subtitle);
        Shot(w, "3-game-with-mod");

        vm.SelectedOther = vm.Other[0];
        Assert.IsType<LibraryViewModel>(vm.Page);
        Shot(w, "4-library");
        vm.SelectedOther = vm.Other[1];
        Shot(w, "5-settings");
        w.Close();
    }
}
