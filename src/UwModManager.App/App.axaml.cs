using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UwModManager.App.ViewModels;
using UwModManager.App.Views;
using UwModManager.Core;

namespace UwModManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var vm = new MainViewModel(new Manager(), new Dialogs(window));
            window.DataContext = vm;
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
