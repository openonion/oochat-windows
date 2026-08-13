using System.Threading.Tasks;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// A page that can be re-pointed at whatever is now selected instead of being rebuilt.
///
/// Most "navigations" in this app are not navigations at all — picking another conversation or
/// another agent lands on the page you are already looking at. Doing that through the Frame
/// (<c>Navigate</c> with <c>forceReload</c>) throws the live page away and constructs a new one:
/// a new XAML tree, a new <c>ChatComposer</c> with its Win2D canvas and audio graph, a new view
/// model, new bindings — per click. Clicking through a sidebar full of sessions piles those up
/// far faster than the GC clears them (the managed objects hold native XAML resources behind
/// them), which is exactly the memory climb this interface exists to prevent.
///
/// <see cref="MainWindow"/> prefers this over a reload-navigation whenever the target page type
/// is the one already on screen. Implementers must be safe to call repeatedly and concurrently:
/// a user clicking quickly starts a second <see cref="ReloadAsync"/> while the first is still
/// awaiting the database, and only the newest one may touch the page's state (the view models
/// here do that with a load-generation counter).
///
/// A page that <i>wants</i> to be rebuilt on every visit — because a fresh instance is how it
/// resets its own transient UI, as HomePage does with the add-agent form — simply doesn't
/// implement this.
/// </summary>
public interface IReloadablePage
{
    Task ReloadAsync();
}
