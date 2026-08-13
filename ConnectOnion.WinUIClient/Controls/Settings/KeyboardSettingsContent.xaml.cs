using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Settings → Keyboard: the whole shortcut catalog, with the rows the app dispatches itself made
/// rebindable and the rest shown read-only with the reason they cannot be. Reads the same catalog
/// and the same <see cref="KeyboardShortcutService"/> as the Ctrl+Shift+/ dialog, so the two can
/// never disagree about what is bound.
/// </summary>
public sealed partial class KeyboardSettingsContent : UserControl
{
    public KeyboardSettingsViewModel Vm { get; } = new(AppServices.Shortcuts);

    public KeyboardSettingsContent() => InitializeComponent();

    /// <summary>Called when the category is entered: drop the previous visit's query, and re-read
    /// every row in case the bindings moved while this pane was hidden.</summary>
    public void Refresh()
    {
        SearchBox.Text = "";
        Vm.RefreshRows();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => Vm.SearchText = SearchBox.Text;

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
        => await Vm.ResetAllAsync();
}
