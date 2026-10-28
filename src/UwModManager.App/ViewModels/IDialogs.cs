using UltimateEditor.Modding;

namespace UwModManager.App.ViewModels;

/// <summary>What the view models ask of the window: pickers, questions and messages.</summary>
public interface IDialogs
{
    Task<string?> PickFolder(string title);
    Task<string?> PickFile(string title, string patternName, params string[] patterns);
    Task<string?> SaveFile(string title, string suggestedName, string extension);
    /// <summary>A question with buttons; returns the index of the button chosen (-1: closed).</summary>
    Task<int> Ask(string title, string text, params string[] buttons);
    Task Tell(string title, string text);
    /// <summary>The details of a new mod (id, name, version, authors, description); null: cancelled.</summary>
    Task<ModManifest?> AskDetails(string title, ModManifest defaults);
}
