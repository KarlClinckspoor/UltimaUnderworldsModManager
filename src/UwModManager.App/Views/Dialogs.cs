using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using UltimateEditor.Modding;
using UwModManager.App.ViewModels;

namespace UwModManager.App.Views;

/// <summary>The window's pickers and small question windows.</summary>
public sealed class Dialogs : IDialogs
{
    readonly Window owner;
    public Dialogs(Window owner) { this.owner = owner; }

    public async Task<string?> PickFolder(string title)
    {
        var r = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count == 0 ? null : r[0].TryGetLocalPath();
    }

    public async Task<string?> PickFile(string title, string patternName, params string[] patterns)
    {
        var r = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(patternName) { Patterns = patterns }, FilePickerFileTypes.All },
        });
        return r.Count == 0 ? null : r[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFile(string title, string suggestedName, string extension)
    {
        var f = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension });
        return f?.TryGetLocalPath();
    }

    public async Task<int> Ask(string title, string text, params string[] buttons)
    {
        int chosen = -1;
        var w = Small(title);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 });
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int k = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(6, 0, 0, 6), MinWidth = 90 };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => { chosen = k; w.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.Content = panel;
        await w.ShowDialog(owner);
        return chosen;
    }

    public async Task Tell(string title, string text) => await Ask(title, text, "OK");

    public async Task<ModManifest?> AskDetails(string title, ModManifest d)
    {
        ModManifest? result = null;
        var w = Small(title);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(20), MinWidth = 480 };
        TextBox Field(int row, string label, string value, string hint)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            var t = new TextBox { Text = value, PlaceholderText = hint, Margin = new Thickness(0, 4) };
            Grid.SetRow(l, row); Grid.SetRow(t, row); Grid.SetColumn(t, 1);
            grid.Children.Add(l); grid.Children.Add(t);
            return t;
        }
        var name = Field(0, "Name", d.Name, "Gate of Dawn");
        var authors = Field(1, "Author(s)", string.Join(", ", d.Authors), "comma separated");
        var id = Field(2, "Id", d.Id, "karl.gate-of-dawn (from the author and name if empty)");
        var version = Field(3, "Version", d.Version, "1.0.0");
        var desc = Field(4, "Description", d.Description, "");
        desc.AcceptsReturn = true; desc.TextWrapping = Avalonia.Media.TextWrapping.Wrap; desc.Height = 80;
        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        ok.Classes.Add("accent");
        Grid.SetRow(ok, 5); Grid.SetColumn(ok, 1);
        grid.Children.Add(ok);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            var authorList = (authors.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            result = new ModManifest
            {
                Name = name.Text.Trim(), Authors = authorList, Version = string.IsNullOrWhiteSpace(version.Text) ? "1.0.0" : version.Text.Trim(),
                Description = desc.Text ?? "", Game = d.Game,
                Id = string.IsNullOrWhiteSpace(id.Text) ? ModBuilder.IdFrom(authorList.FirstOrDefault() ?? "", name.Text) : id.Text.Trim(),
            };
            w.Close();
        };
        w.Content = grid;
        await w.ShowDialog(owner);
        return result;
    }

    static Window Small(string title) => new()
    {
        Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
    };
}
