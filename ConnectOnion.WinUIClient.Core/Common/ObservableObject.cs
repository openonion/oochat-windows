namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Project-local base for view models and observable models. It now derives from
/// CommunityToolkit.Mvvm's <see cref="CommunityToolkit.Mvvm.ComponentModel.ObservableObject"/>,
/// which supplies the same <c>SetProperty</c>/<c>OnPropertyChanged</c> surface the hand-rolled
/// version used to expose — so existing manual property setters keep compiling unchanged — and,
/// crucially, satisfies the requirement that lets the <c>[ObservableProperty]</c> /
/// <c>[RelayCommand]</c> source generators run on subclasses. Kept as a named base so the many
/// <c>: ObservableObject</c> declarations across the app need no churn.
/// </summary>
public abstract class ObservableObject : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
}
