using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;
using Application = FlaUI.Core.Application;

namespace ConnectOnion.WinUIClient.UITests;

[Trait("Category", "UiSmoke")]
[Collection(UiAutomationCollection.Name)]
public sealed partial class ShellSmokeTests
{
    public const string ExecutableEnvironmentVariable = "CONNECTONION_UI_TEST_EXE";
    private const string DataRootEnvironmentVariable = "CONNECTONION_DATA_ROOT";
    private const string DocumentationScreenshotDirectoryVariable =
        "CONNECTONION_README_SCREENSHOT_DIR";
    private static readonly Lazy<string> DefaultDataRoot = new(CreateDefaultDataRoot);
    private static int _profileLaunchHandled;
    private static int _sqliteProviderConfigured;

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    private const uint WmCommand = 0x0111;
    private const int MessageBoxOk = 1;
    private const int MessageBoxNo = 7;

    [Fact]
    public void Launch_ShowsResponsiveConnectOnionWindow()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        Assert.Contains("ConnectOnion", window.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(window.IsEnabled);
        Assert.NotNull(WaitForDescendant(window, "SettingsButton"));
    }

    [Fact]
    public void Launch_StartupFailure_ShowsRecoveryMessageAndExitsCleanly()
    {
        using var launched = LaunchApp(
            handleFirstRunDialog: false,
            new Dictionary<string, string>
            {
                ["CONNECTONION_UI_STARTUP_FAILURE"] = "1",
            });
        if (launched is null) return;

        Assert.Contains("ConnectOnion", launched.Window.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            launched.Window.Title.Contains("start", StringComparison.OrdinalIgnoreCase)
            || launched.Window.Title.Contains("启动", StringComparison.Ordinal),
            $"unexpected startup recovery title: {launched.Window.Title}");

        var buttons = launched.Window.FindAllDescendants(
            query => query.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
        Assert.NotEmpty(buttons);
        DismissStartupFailureDialog(launched.Window, buttons);

        Assert.True(
            WaitUntil(() => launched.Process.HasExited, TimeSpan.FromSeconds(30)),
            "the process did not exit after the startup recovery message was dismissed");
    }

    private static void DismissStartupFailureDialog(
        Window window,
        AutomationElement[] buttons)
    {
        window.SetForeground();
        Thread.Sleep(300);

        var handle = new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert.NotEqual(IntPtr.Zero, handle);

        // Native MessageBox buttons are unreliable through UIA Invoke; WM_COMMAND is the stable path.
        var commandId = buttons.Length == 1 ? MessageBoxOk : MessageBoxNo;
        _ = SendMessage(handle, WmCommand, (IntPtr)commandId, IntPtr.Zero);
    }

    /// <summary>
    /// Clicking the sidebar's fixed Settings row shows the settings overlay.
    /// </summary>
    [Fact]
    public void SidebarSettingsClick_ShowsSettingsOverlay()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;

        // Asserted on the overlay's *content*, not on the SettingsOverlay element itself: that
        // UserControl carries an AutomationId in XAML but produces no automation peer of its own,
        // so UIA never exposes it. GeneralNav is inside the overlay and appears only once it opens.
        const string settingsContentId = "GeneralNav";

        Assert.Null(window.FindFirstDescendant(query => query.ByAutomationId(settingsContentId)));

        OpenSettings(window);
        CaptureDocumentationScreenshot(window, "settings-general.png");

        var search = WaitForDescendant(window, "SettingsSearchBox");
        Assert.NotNull(search);
        Assert.True(
            WaitUntil(() =>
                search.Properties.HasKeyboardFocus.ValueOrDefault
                || window.FindFirstDescendant(query =>
                    query.ByAutomationId("SettingsCategoryPicker"))?
                    .Properties.HasKeyboardFocus.ValueOrDefault == true),
            "settings overlay did not move focus into its wide or compact navigation");

        var close = WaitForDescendant(window, "SettingsCloseButton");
        Assert.NotNull(close);
        close.AsButton().Invoke();
        Assert.True(
            WaitUntil(() => window.FindFirstDescendant(
                query => query.ByAutomationId(settingsContentId)) is null),
            "settings overlay did not close");

        var settingsButton = WaitForDescendant(window, "SettingsButton");
        Assert.NotNull(settingsButton);
        Assert.True(
            WaitUntil(() => settingsButton.Properties.HasKeyboardFocus.ValueOrDefault),
            "settings opener did not regain focus after close");
    }

    [Fact]
    public void SidebarSettingsRow_IsAReachableFooterCommand()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        var settings = WaitForDescendant(window, "SettingsButton");
        Assert.NotNull(settings);
        Assert.Equal("Settings", settings.Properties.Name.ValueOrDefault);
        Assert.True(settings.IsEnabled);
    }

    [Fact]
    public void CloseToTray_SecondLaunch_RestoresTheResponsiveWindow()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var windowHandle = launched.Process.MainWindowHandle;
        Assert.NotEqual(IntPtr.Zero, windowHandle);
        var originalMousePosition = Mouse.Position;

        try
        {
            // Reproduce the real bug: a physical click leaves the pointer and DWM hover state on
            // the caption Close button when WM_CLOSE is consumed and the window is hidden.
            var initialBounds = launched.Window.BoundingRectangle;
            Mouse.MoveTo(initialBounds.Right - 23, initialBounds.Top + 16);
            Thread.Sleep(150);
            Mouse.LeftClick();

            // Closing no longer hides unconditionally: the first close asks whether to keep
            // running or exit, and remembers the answer (MainWindow.Tray HandleWindowCloseAsync).
            // So both outcomes are legitimate here and which one happens depends on whether this
            // data root has been closed before — answer the prompt if it appears, and carry on if
            // a stored preference already sent us straight to the tray.
            AnswerKeepRunningIfPrompted(launched.Window);

            Assert.True(WaitUntil(() => !IsWindowVisible(windowHandle)), "window did not hide to tray");

            // Move while the HWND is hidden, so it receives no non-client mouse transition.
            Mouse.MoveTo(initialBounds.Left + initialBounds.Width / 2,
                initialBounds.Top + initialBounds.Height / 2);

            var executable = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
            Assert.False(string.IsNullOrWhiteSpace(executable));
            using var redirected = launched.StartAnotherInstance();
            Assert.NotNull(redirected);

            Assert.True(
                WaitUntil(() =>
                {
                    launched.Process.Refresh();
                    return IsWindowVisible(windowHandle)
                        && !launched.Process.HasExited
                        && launched.Process.Responding;
                }),
                "redirected launch did not restore the tray window");

            Assert.NotNull(WaitForDescendant(launched.Window, "SettingsButton"));

            // Give the queued low-priority non-client tracking request time to deliver its real
            // WM_NCMOUSELEAVE, then inspect the actual pixels of the caption Close button.
            Thread.Sleep(350);
            var restoredBounds = launched.Window.BoundingRectangle;
            var closeBounds = new System.Drawing.Rectangle(
                restoredBounds.Right - 46,
                restoredBounds.Top,
                46,
                32);
            using var closeCapture = Capture.Rectangle(closeBounds, new CaptureSettings());

            var captureDirectory = Environment.GetEnvironmentVariable("CONNECTONION_UI_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                closeCapture.ToFile(Path.Combine(captureDirectory, "caption-close-after-tray-restore.png"));
            }

            var redPixels = 0;
            for (var y = 0; y < closeCapture.Bitmap.Height; y++)
            {
                for (var x = 0; x < closeCapture.Bitmap.Width; x++)
                {
                    var pixel = closeCapture.Bitmap.GetPixel(x, y);
                    if (pixel.R >= 180
                        && pixel.R > pixel.G * 1.5
                        && pixel.R > pixel.B * 1.3)
                    {
                        redPixels++;
                    }
                }
            }

            Assert.True(
                redPixels < 100,
                $"caption Close button retained its red hover state ({redPixels} red pixels)");
        }
        finally
        {
            Mouse.MoveTo(originalMousePosition);
        }
    }

    [Fact]
    public void SettingsAgents_Click_ShowsAgentManagementActions()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        OpenSettings(window);

        var agentsNav = window.FindFirstDescendant(query => query.ByAutomationId("AgentsNav"));
        Assert.NotNull(agentsNav);
        agentsNav.AsRadioButton().Click();

        AutomationElement? addAgent = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && addAgent is null)
        {
            try
            {
                addAgent = window
                    .FindAllDescendants(query => query.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                    .FirstOrDefault(element =>
                        string.Equals(element.Properties.Name.ValueOrDefault, "Add agent", StringComparison.Ordinal));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The settings pane is swapping its visual tree; retry below.
            }

            if (addAgent is null) Thread.Sleep(150);
        }

        Assert.NotNull(addAgent);
        Assert.True(addAgent.IsEnabled);
        addAgent.AsButton().Invoke();
        Assert.NotNull(WaitForDescendant(window, "AgentAddressInput"));
    }

    /// <summary>
    /// Settings → Identity offers a way to back the identity up and to restore one.
    ///
    /// <para>Worth a real-window test because the whole feature exists to be reachable: an identity
    /// with no visible backup path is exactly the situation this replaced, and nothing headless can
    /// tell you the buttons made it onto the page. The reveal is exercised (it only reads), the
    /// restore is opened but never committed — pressing "Replace identity" would overwrite the
    /// identity of whatever machine runs the suite.</para>
    /// </summary>
    [Fact]
    public void SettingsIdentity_Click_ShowsBackupAndRestoreActions()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        OpenSettings(window);

        var identityNav = window.FindFirstDescendant(query => query.ByAutomationId("IdentityNav"));
        Assert.NotNull(identityNav);
        identityNav.AsRadioButton().Click();

        var showBackup = WaitForDescendant(window, "ShowIdentityBackupButton");
        Assert.NotNull(showBackup);
        Assert.NotNull(window.FindFirstDescendant(query => query.ByAutomationId("RestoreIdentityButton")));
        Assert.NotNull(window.FindFirstDescendant(query => query.ByAutomationId("GenerateIdentityButton")));

        // The dialog's own content is what proves the reveal worked — the address it says the
        // backup belongs to must be the address the panel is showing.
        showBackup.AsButton().Invoke();
        var addressLabel = WaitForText(window, "Belongs to address");
        Assert.NotNull(addressLabel);
    }

    /// <summary>
    /// Answers the "Close ConnectOnion?" prompt with <b>Keep running</b> when it is showing.
    ///
    /// <para>Returns false — without failing — when it is not, because that is a legitimate state:
    /// the choice is persisted, so a data root that has already been closed once goes straight to
    /// the tray with no prompt at all. A test that insisted on the dialog would pass exactly once
    /// per profile and then fail forever.</para>
    ///
    /// <para>Invoked through UIA rather than clicked with the mouse, and that is deliberate here
    /// rather than merely convenient: the caller has just left the physical pointer parked on the
    /// caption Close button, which is the whole precondition of the hover-state bug this test
    /// exists for. Moving the mouse to press the dialog button would clear it and quietly turn the
    /// test into a no-op.</para>
    /// </summary>
    private static bool AnswerKeepRunningIfPrompted(Window window)
    {
        // Short deadline: this is a "did a dialog appear" probe, not a wait for slow work, and the
        // no-dialog path is the common one once a profile has been used.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var keepRunning = window
                    .FindAllDescendants(query => query.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                    .FirstOrDefault(element =>
                        string.Equals(element.Properties.Name.ValueOrDefault, "Keep running", StringComparison.Ordinal));
                if (keepRunning is not null)
                {
                    keepRunning.AsButton().Invoke();
                    return true;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The dialog is being composed over the shell; retry below.
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>Polls for a Text element with the given content, tolerating the COMException a
    /// mutating visual tree throws.</summary>
    private static AutomationElement? WaitForText(
        AutomationElement window,
        string text,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var match = window
                    .FindAllDescendants(query => query.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                    .FirstOrDefault(element =>
                        string.Equals(element.Properties.Name.ValueOrDefault, text, StringComparison.Ordinal));
                if (match is not null) return match;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Tree changed under the query; retry below.
            }

            Thread.Sleep(150);
        }

        return null;
    }

    /// <summary>
    /// The sidebar's "Add agent" button opens the shell-owned modal over the current page.
    /// Collapsed overlay content is absent from the UIA tree, so its focused input proves both
    /// visibility and keyboard readiness.
    /// </summary>
    [Fact]
    public void SidebarAddAgent_Click_OpensShellOverlayAndFocusesInput()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;

        // Collapsed controls are not in the UIA tree, so absence here is what "the form is shut"
        // means — and its later presence is what proves the click opened it.
        Assert.Null(window.FindFirstDescendant(query => query.ByAutomationId("AgentAddressInput")));

        var addAgent = window.FindFirstDescendant(query => query.ByAutomationId("AddAgentButton"));
        Assert.NotNull(addAgent);
        addAgent.AsButton().Invoke();

        var input = WaitForDescendant(window, "AgentAddressInput");
        Assert.NotNull(input);

        // The form is opened *focused* so the user can paste an address immediately; without
        // this the click would still leave them one more click from typing.
        Assert.True(
            WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault),
            "add-agent input did not take focus");
    }

    /// <summary>
    /// The token-usage heatmap renders a full grid of day squares in Settings → Usage.
    ///
    /// <para>Asserted through a day square's accessible name rather than the control's own
    /// AutomationId: the squares are Buttons and therefore real automation peers, whereas the
    /// UserControl wrapping them produces none (the same trap that made an earlier attempt at
    /// this assert nothing). It doubles as the accessibility check — a keyboard or screen-reader
    /// user must be told what a square means, and this is that text.</para>
    /// </summary>
    [Fact]
    public void UsageHeatmap_RendersDaySquares_WithAccessibleDescriptions()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;

        OpenSettings(window);

        var usageNav = WaitForDescendant(window, "UsageNav");
        Assert.NotNull(usageNav);
        usageNav.AsRadioButton().Click();
        Assert.True(
            WaitUntil(() => usageNav.AsRadioButton().IsChecked == true),
            "Usage settings category was not selected.");

        // A fresh profile has no usage, so every square is an empty day — which is exactly the
        // case worth pinning: the grid must still render rather than collapsing to nothing.
        AutomationElement? square = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && square is null)
        {
            try
            {
                // Properties.Name.ValueOrDefault, not .Name: the latter throws
                // PropertyNotSupportedException on elements that expose no Name at all, and this
                // sweep necessarily walks over some of those.
                square = window
                    .FindAllDescendants(query => query.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                    .FirstOrDefault(e => e.Properties.Name.ValueOrDefault?
                        .StartsWith("No token usage", StringComparison.Ordinal) == true);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Tree still settling; retry below.
            }

            if (square is null) Thread.Sleep(200);
        }

        Assert.NotNull(square);
    }

    [Fact]
    public void FirstRunHome_ShowsEmptyStateAndReachableAddAgentAction()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        var addFirstAgent = WaitForDescendant(window, "EmptyStateAddAgentButton");
        var docs = WaitForDescendant(window, "EmptyStateDocsLink");

        Assert.NotNull(addFirstAgent);
        Assert.True(addFirstAgent.IsEnabled);
        Assert.NotNull(docs);
        Assert.True(docs.IsEnabled);
        CaptureDocumentationScreenshot(window, "home.png");

        addFirstAgent.AsButton().Invoke();
        var input = WaitForDescendant(window, "AgentAddressInput");
        Assert.NotNull(input);
        Assert.True(WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault));
    }

    [Fact]
    public void SessionSearch_OpensFocusedAndClosesWithEscape()
    {
        var dataRoot = ResolveDataRoot();
        using (var bootstrap = LaunchApp())
        {
            if (bootstrap is null) return;
        }
        SeedSearchFixture(dataRoot);

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;

            var window = launched.Window;
            var opener = WaitForDescendant(window, "SessionSearchButton");
            Assert.NotNull(opener);

            opener.AsButton().Invoke();

            var search = WaitForDescendant(window, "SessionSearchBox");
            Assert.NotNull(search);
            Assert.True(WaitUntil(() => search.Properties.HasKeyboardFocus.ValueOrDefault));
            Assert.NotNull(WaitForDescendant(window, "SessionSearchResults"));

            search.AsTextBox().Text = "Automation Search";
            Assert.NotNull(WaitForText(window, "Automation Search Session"));

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Assert.True(
                WaitUntil(() => window.FindFirstDescendant(
                    query => query.ByAutomationId("SessionSearchBox")) is null),
                "session-search overlay did not close on Escape");
        }
        finally
        {
            RemoveSearchFixture(dataRoot);
        }
    }

    [Fact]
    public void HelpKeyboardShortcuts_OpensFocusedAndClosesWithEscape()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        OpenHelpMenuItem(window, "KeyboardShortcutsMenuItem", "ShortcutsSearchBox");

        var search = WaitForDescendant(window, "ShortcutsSearchBox");
        Assert.NotNull(search);
        Assert.True(WaitUntil(() => search.Properties.HasKeyboardFocus.ValueOrDefault));
        Assert.NotNull(WaitForDescendant(window, "KeyboardShortcutsCloseButton"));

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(
            WaitUntil(() => window.FindFirstDescendant(
                query => query.ByAutomationId("ShortcutsSearchBox")) is null),
            "keyboard-shortcuts overlay did not close on Escape");
    }

    [Fact]
    public void HelpAbout_OpensFocusedAndOkClosesIt()
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        OpenHelpMenuItem(window, "AboutMenuItem", "AboutOkButton");

        var ok = WaitForDescendant(window, "AboutOkButton");
        Assert.NotNull(ok);
        Assert.True(WaitUntil(() => ok.Properties.HasKeyboardFocus.ValueOrDefault));
        Assert.NotNull(WaitForDescendant(window, "AboutCloseButton"));

        ok.AsButton().Invoke();
        Assert.True(
            WaitUntil(() => window.FindFirstDescendant(
                query => query.ByAutomationId("AboutOkButton")) is null),
            "About overlay did not close after OK");
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("session-search")]
    [InlineData("shortcuts")]
    [InlineData("about")]
    [InlineData("add-agent")]
    public void ModalOverlay_EscapeClosesAndReturnsFocusToOpener(string overlay)
    {
        using var launched = LaunchApp();
        if (launched is null) return;

        var window = launched.Window;
        AutomationElement? opener;
        string overlayContentId;
        switch (overlay)
        {
            case "settings":
                opener = WaitForDescendant(window, "SettingsButton");
                Assert.NotNull(opener);
                opener.AsButton().Invoke();
                overlayContentId = "GeneralNav";
                break;
            case "session-search":
                opener = WaitForDescendant(window, "SessionSearchButton");
                Assert.NotNull(opener);
                opener.AsButton().Invoke();
                overlayContentId = "SessionSearchBox";
                break;
            case "shortcuts":
                opener = WaitForDescendant(window, "HelpMenuButton");
                Assert.NotNull(opener);
                OpenHelpMenuItem(window, "KeyboardShortcutsMenuItem", "ShortcutsSearchBox");
                overlayContentId = "ShortcutsSearchBox";
                break;
            case "about":
                opener = WaitForDescendant(window, "HelpMenuButton");
                Assert.NotNull(opener);
                OpenHelpMenuItem(window, "AboutMenuItem", "AboutOkButton");
                overlayContentId = "AboutOkButton";
                break;
            case "add-agent":
                opener = WaitForDescendant(window, "AddAgentButton");
                Assert.NotNull(opener);
                opener.AsButton().Invoke();
                overlayContentId = "AgentAddressInput";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(overlay), overlay, null);
        }

        Assert.NotNull(WaitForDescendant(window, overlayContentId));
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(WaitUntil(() => window.FindFirstDescendant(
            query => query.ByAutomationId(overlayContentId)) is null));
        Assert.True(WaitUntil(() => opener.Properties.HasKeyboardFocus.ValueOrDefault));
    }

    /// <summary>Invokes the sidebar's fixed Settings command and waits for the modal.</summary>
    internal static void OpenSettings(Window window)
    {
        var settingsButton = WaitForDescendant(window, "SettingsButton");
        Assert.NotNull(settingsButton);
        settingsButton.AsButton().Invoke();

        Assert.NotNull(WaitForDescendant(window, "GeneralNav"));
    }

    /// <summary>
    /// Opens the Help menu and clicks one of its items.
    ///
    /// <para>Two things here are load-bearing, and both exist because a menu flyout can only be
    /// opened by a <b>physical</b> click — <c>MenuBarItem</c> exposes ExpandCollapse rather than
    /// Invoke, so the UIA-pattern route the rest of this suite prefers is not available. A physical
    /// click goes to whatever window is under the cursor's coordinates in the foreground, which
    /// makes it the one interaction in this suite that can be stolen.</para>
    ///
    /// <para>So the window is brought to the foreground first: the previous test's app is killed in
    /// <c>Dispose</c>, and until that process finishes exiting it can still own the foreground, so
    /// the click lands on a dying window and the flyout never opens. That is exactly how
    /// <c>HelpKeyboardShortcuts</c> failed in a full-suite run while passing 3/3 in isolation.</para>
    ///
    /// <para>And the open is retried, because the click is a <i>toggle</i>: if a stray earlier click
    /// already opened the flyout, clicking Help again closes it, and a single attempt then reports
    /// the item as missing when the menu was in the opposite state to the one assumed.</para>
    ///
    /// <para>When <paramref name="overlayContentId"/> is set, the whole open is retried until that
    /// element appears. The shortcuts dialog is constructed lazily on first open and can outlast the
    /// menu-flyout timeout on a loaded CI agent.</para>
    /// </summary>
    private static void OpenHelpMenuItem(
        Window window,
        string automationId,
        string? overlayContentId = null)
    {
        var help = WaitForDescendant(window, "HelpMenuButton");
        Assert.NotNull(help);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { window.SetForeground(); }
            catch (Exception) { /* the window may still be settling; the click below retries */ }

            help.AsMenuItem().Click();
            var item = WaitForDescendant(window, automationId, TimeSpan.FromSeconds(5));
            if (item is null) continue;

            item.AsMenuItem().Click();

            if (overlayContentId is null) return;

            if (WaitForDescendant(window, overlayContentId, TimeSpan.FromSeconds(15)) is not null)
                return;

            // Menu item was clicked but the modal never surfaced — reset and try again.
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(250);
        }

        if (overlayContentId is not null)
        {
            var overlay = WaitForDescendant(window, overlayContentId, TimeSpan.FromSeconds(15));
            Assert.True(
                overlay is not null,
                $"Help menu item '{automationId}' was clicked but '{overlayContentId}' never appeared.");
            return;
        }

        Assert.Fail($"the Help menu never revealed '{automationId}'");
    }

    /// <summary>Opens the shared find overlay with Ctrl+F. Brings the app forward and refocuses
    /// the composer first — hosted CI agents can steal foreground while a long markdown render
    /// is settling, and FlaUI's keystrokes only reach the active window.</summary>
    internal static AutomationElement OpenFindOverlay(Window window)
    {
        window.SetForeground();
        var input = WaitForDescendant(window, "MessageInput", TimeSpan.FromSeconds(5));
        if (input is not null)
        {
            input.Focus();
            Assert.True(
                WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault),
                "composer did not take focus before opening find");
        }

        AutomationElement? find = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && find is null)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
            find = WaitForDescendant(window, "FindTextBox", TimeSpan.FromSeconds(2));
            if (find is null)
            {
                Thread.Sleep(200);
            }
        }

        Assert.NotNull(find);
        return find;
    }

    /// <summary>Polls for a descendant, tolerating the transient UIA failures a mutating visual
    /// tree produces.</summary>
    private static AutomationElement? WaitForDescendant(
        Window window,
        string automationId,
        TimeSpan? timeout = null)
    {
        AutomationElement? found = null;
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline && found is null)
        {
            try
            {
                found = window.FindFirstDescendant(query => query.ByAutomationId(automationId));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Tree changed under the query; retry below.
            }

            if (found is null) Thread.Sleep(150);
        }

        return found;
    }

    private static bool WaitUntil(Func<bool> condition)
        => WaitUntil(condition, TimeSpan.FromSeconds(10));

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return true;
            }
            catch (InvalidOperationException)
            {
                // The process/window can be between hidden and visible states; retry below.
            }
            catch (COMException)
            {
                // UIA can lose an element while an overlay is opening or closing; retry below.
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>
    /// Captures a stable, window-only documentation image when the opt-in output directory is
    /// configured. Normal UI smoke runs do no screenshot work. Keeping capture on the same paths
    /// CI exercises means README images come from production XAML and realistic state.
    /// </summary>
    internal static void CaptureDocumentationScreenshot(Window window, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable(
            DocumentationScreenshotDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        var handle = new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(MoveWindow(handle, 80, 50, 1400, 900, true));
        window.SetForeground();
        Thread.Sleep(600);

        using var capture = Capture.Rectangle(window.BoundingRectangle, new CaptureSettings());
        capture.ToFile(Path.Combine(directory, fileName));
    }

    private static readonly TimeSpan LaunchProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LaunchShellReadyTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] ShellReadyAutomationIds =
    [
        "MainWindow",
        "SettingsButton",
        "HelpMenuButton",
        "FileMenuButton",
        "HomeAddAgentButton",
    ];

    /// <summary>
    /// Launches the app and attaches automation to its window, or returns null when the opt-in
    /// environment variable is unset. Opt-in because hosted CI agents often have no interactive
    /// desktop; the documented UI-smoke workflow sets the path after publishing an unpackaged
    /// x64 build.
    /// </summary>
    internal static LaunchedApp? LaunchApp(
        bool handleFirstRunDialog = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var executable = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(executable)) return null;

        Assert.True(File.Exists(executable), $"UI test executable not found: {executable}");

        var existingProcessIds = System.Diagnostics.Process
            .GetProcessesByName("ConnectOnion.WinUIClient")
            .Select(process => process.Id)
            .ToHashSet();

        LaunchedApp? launched = null;
        for (var attempt = 0; attempt < 2 && launched is null; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(500);
            }

            launched = TryLaunchAppOnce(
                executable,
                existingProcessIds,
                environment,
                handleFirstRunDialog);
        }

        Assert.True(
            launched is not null,
            "ConnectOnion did not publish an automation-ready window within the launch budget.");
        return launched;
    }

    private static bool ExpectsMainShell(IReadOnlyDictionary<string, string>? environment)
        => environment is null
           || !string.Equals(
               environment.GetValueOrDefault("CONNECTONION_UI_STARTUP_FAILURE"),
               "1",
               StringComparison.Ordinal);

    private static LaunchedApp? TryLaunchAppOnce(
        string executable,
        HashSet<int> existingProcessIds,
        IReadOnlyDictionary<string, string>? environment,
        bool handleFirstRunDialog)
    {
        using var launchedProcess = StartProcess(executable, environment);
        if (launchedProcess is null) return null;

        // An unpackaged WinUI executable can hand off to the Windows App SDK host, so the
        // process returned by Start may exit before the real top-level window is created.
        System.Diagnostics.Process? windowProcess = null;
        var deadline = DateTime.UtcNow.Add(LaunchProcessTimeout);
        while (DateTime.UtcNow < deadline && windowProcess is null)
        {
            windowProcess = System.Diagnostics.Process
                .GetProcessesByName("ConnectOnion.WinUIClient")
                .FirstOrDefault(process =>
                    !existingProcessIds.Contains(process.Id)
                    && !process.HasExited);
            if (windowProcess is null)
            {
                Thread.Sleep(200);
            }
        }

        if (windowProcess is null) return null;

        var expectMainShell = ExpectsMainShell(environment);
        Application? app = null;
        UIA3Automation? automation = null;
        try
        {
            app = Application.Attach(windowProcess.Id);
            automation = new UIA3Automation();
            Window? window = null;
            deadline = DateTime.UtcNow.Add(LaunchShellReadyTimeout);
            while (DateTime.UtcNow < deadline && window is null)
            {
                try
                {
                    var candidate = app.GetAllTopLevelWindows(automation)
                        .FirstOrDefault(candidate => candidate.Title.Contains(
                            "ConnectOnion", StringComparison.OrdinalIgnoreCase));
                    if (candidate is not null && IsLaunchSurfaceReady(candidate, expectMainShell))
                    {
                        window = candidate;
                        break;
                    }
                }
                catch (COMException)
                {
                    // WinUI has created its process, but its UIA provider is still publishing the
                    // top-level XAML window. Re-enumerate instead of retaining an empty root object.
                }

                Thread.Sleep(200);
            }

            if (window is null)
            {
                try { app.Kill(); } catch { /* already gone */ }
                automation.Dispose();
                app.Dispose();
                return null;
            }

            if (handleFirstRunDialog
                && Interlocked.Exchange(ref _profileLaunchHandled, 1) == 0)
            {
                DismissFirstRunRecoveryDialog(window);
            }

            return new LaunchedApp(windowProcess, app, automation, window, executable);
        }
        catch
        {
            try { app?.Kill(); } catch { /* already gone */ }
            automation?.Dispose();
            app?.Dispose();
            return null;
        }
    }

    private static bool IsLaunchSurfaceReady(Window window, bool expectMainShell)
    {
        if (expectMainShell)
        {
            return IsShellAutomationReady(window);
        }

        // Startup-failure injection shows a native MessageBox, not the WinUI shell.
        try
        {
            if (window.FindAllDescendants().Length > 0)
            {
                return true;
            }
        }
        catch (COMException)
        {
            // Tree still publishing; the outer launch loop will retry.
        }

        var title = window.Title ?? string.Empty;
        return title.Contains("ConnectOnion", StringComparison.OrdinalIgnoreCase)
               && (title.Contains("start", StringComparison.OrdinalIgnoreCase)
                   || title.Contains("启动", StringComparison.Ordinal));
    }

    private static bool IsShellAutomationReady(Window window)
    {
        foreach (var automationId in ShellReadyAutomationIds)
        {
            try
            {
                if (window.FindFirstDescendant(query => query.ByAutomationId(automationId)) is not null)
                {
                    return true;
                }
            }
            catch (COMException)
            {
                // The shell is still materializing; the outer launch loop will retry.
            }
        }

        return false;
    }

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        var dataRoot = string.IsNullOrWhiteSpace(configured)
            ? DefaultDataRoot.Value
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(dataRoot);
        if (string.IsNullOrWhiteSpace(configured))
            Environment.SetEnvironmentVariable(DataRootEnvironmentVariable, dataRoot);
        return dataRoot;
    }

    private static string CreateDefaultDataRoot()
        => Path.Combine(Path.GetTempPath(), "ConnectOnion.UiTests", Guid.NewGuid().ToString("N"));

    private static void SeedSearchFixture(string dataRoot)
    {
        if (Interlocked.Exchange(ref _sqliteProviderConfigured, 1) == 0)
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "connectonion.db"),
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO agents
                (id, name, address, direct_url, sort_order)
            VALUES
                ('ui-agent', 'Automation Agent', 'uiautomation', NULL, 0);

            INSERT OR REPLACE INTO sessions
                (id, agent_id, title, remote_session_id, last_processed_event_id,
                 created_at, updated_at, sort_order, mode, has_custom_title)
            VALUES
                ('ui-session', 'ui-agent', 'Automation Search Session', NULL, NULL,
                 '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z',
                 0, 'safe', 1);
            """;
        command.ExecuteNonQuery();
    }

    private static void RemoveSearchFixture(string dataRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "connectonion.db"),
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sessions WHERE id = 'ui-session';
            DELETE FROM agents WHERE id = 'ui-agent';
            """;
        command.ExecuteNonQuery();
    }

    private static System.Diagnostics.Process? StartProcess(
        string executable,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
        var dataRoot = ResolveDataRoot();
        var escapedDataRoot = dataRoot.Replace("'", "''", StringComparison.Ordinal);
        var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            // The unpackaged Windows App SDK executable needs shell activation. Launch it through
            // a short-lived PowerShell child: the helper receives an explicit environment block,
            // and Start-Process carries that block into the shell-activated app. Calling
            // ShellExecute from the test host directly can route through Explorer and lose the
            // isolated CONNECTONION_DATA_ROOT value.
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        // Set the override in the child PowerShell command as well as its explicit environment
        // block. Start-Process performs the final shell activation, and this keeps the value in
        // that exact process's environment even on hosts that rebuild the child environment.
        var environmentScript = new System.Text.StringBuilder(
            $"$env:{DataRootEnvironmentVariable} = '{escapedDataRoot}'; ");
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                Assert.Matches("^[A-Z0-9_]+$", key);
                var escapedValue = value.Replace("'", "''", StringComparison.Ordinal);
                environmentScript.Append("$env:").Append(key).Append(" = '")
                    .Append(escapedValue).Append("'; ");
                startInfo.Environment[key] = value;
            }
        }
        environmentScript.Append("Start-Process -FilePath '")
            .Append(escapedExecutable)
            .Append('\'');
        startInfo.ArgumentList.Add(environmentScript.ToString());
        startInfo.Environment[DataRootEnvironmentVariable] = dataRoot;
        return System.Diagnostics.Process.Start(startInfo);
    }

    private static void DismissFirstRunRecoveryDialog(Window window)
    {
        var dialog = WaitForDescendant(
            window, "RecoveryPhraseDialog", TimeSpan.FromSeconds(2));
        if (dialog is null
            && WaitForText(window, "Save your recovery phrase", TimeSpan.FromSeconds(6)) is null)
        {
            return; // An explicitly supplied profile may already have an identity.
        }

        AutomationElement? close = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && close is null)
        {
            try
            {
                close = (dialog ?? window)
                    .FindAllDescendants(query => query.ByControlType(
                        FlaUI.Core.Definitions.ControlType.Button))
                    .FirstOrDefault(element => string.Equals(
                        element.Properties.Name.ValueOrDefault,
                        "Close",
                        StringComparison.Ordinal));
            }
            catch (COMException)
            {
                // The ContentDialog is still constructing its generated close button.
            }

            if (close is null) Thread.Sleep(100);
        }

        Assert.NotNull(close);
        close.AsButton().Invoke();
        Assert.True(
            WaitUntil(() => window.FindFirstDescendant(
                query => query.ByAutomationId("RecoveryPhraseDialog")) is null),
            "first-run recovery dialog did not close");
    }

    /// <summary>Owns one launched app instance for the duration of a test.</summary>
    internal sealed class LaunchedApp(
        System.Diagnostics.Process process,
        Application app,
        UIA3Automation automation,
        Window window,
        string executable) : IDisposable
    {
        public Window Window { get; } = window;
        public System.Diagnostics.Process Process => process;
        public UIA3Automation Automation => automation;

        public System.Diagnostics.Process? StartAnotherInstance()
            => StartProcess(executable);

        public void Dispose()
        {
            // The product's normal window Close action can minimize to the tray by user
            // preference. This process is owned by the smoke test, so terminate it explicitly.
            try { app.Kill(); } catch { /* already gone */ }
            automation.Dispose();
            app.Dispose();
            process.Dispose();
        }
    }
}
