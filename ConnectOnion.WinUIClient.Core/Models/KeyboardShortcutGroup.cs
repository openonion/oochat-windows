namespace ConnectOnion.WinUIClient.Models;

/// <summary>A titled section of related shortcuts (e.g. "Chat"), as shown in the dialog.</summary>
public sealed class KeyboardShortcutGroup
{
    // Plain { get; set; } properties and a parameterless ctor are required, not stylistic:
    // this type is referenced from an x:Bind DataTemplate, and `required`/`init` accessors
    // break the compiler-generated XamlTypeInfo metadata with a confusing CS9035/CS8852.
    public KeyboardShortcutGroup()
    {
    }

    public KeyboardShortcutGroup(string title, IReadOnlyList<KeyboardShortcutItem> shortcuts)
    {
        Title = title;
        Shortcuts = shortcuts;
    }

    public string Title { get; set; } = "";

    public IReadOnlyList<KeyboardShortcutItem> Shortcuts { get; set; } = new List<KeyboardShortcutItem>();
}
