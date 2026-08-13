using System.Globalization;
using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class ThemeContrastContractTests
{
    private static readonly (string Foreground, string Background)[] CriticalPairs =
    [
        ("TextPrimaryColor", "AppBackgroundColor"),
        ("TextPrimaryColor", "SurfacePrimaryColor"),
        ("TextPrimaryColor", "SurfaceSecondaryColor"),
        ("TextPrimaryColor", "SurfaceElevatedColor"),
        ("TextPrimaryColor", "UserMessageBackgroundColor"),
        ("TextPrimaryColor", "AgentMessageBackgroundColor"),
        ("TextPrimaryColor", "SystemMessageBackgroundColor"),
        ("TextPrimaryColor", "ChatInputBackgroundColor"),
        ("TextSecondaryColor", "AppBackgroundColor"),
        ("TextSecondaryColor", "SurfacePrimaryColor"),
        ("TextOnBrandColor", "BrandPrimaryColor"),
        ("SearchMatchTextColor", "SearchMatchBackgroundColor"),
        ("SearchCurrentTextColor", "SearchCurrentBackgroundColor"),
        ("CodeTextColor", "CodeBackgroundColor"),
        ("CodeTextColor", "InlineCodeBackgroundColor"),
        ("ComposerStopButtonForegroundColor", "ComposerStopButtonBackgroundColor"),
        ("AvatarForegroundColor", "AvatarBackgroundColor"),
        ("SuccessColor", "SurfacePrimaryColor"),
        ("WarningColor", "SurfacePrimaryColor"),
        ("DangerColor", "SurfacePrimaryColor"),
        ("InfoColor", "SurfacePrimaryColor"),
        ("AttentionColor", "SurfacePrimaryColor"),
        ("ComposerStatusIdleColor", "ChatInputBackgroundColor"),
        ("ComposerStatusConnectingColor", "ChatInputBackgroundColor"),
        ("ComposerStatusConnectedColor", "ChatInputBackgroundColor"),
        ("ComposerStatusRunningColor", "ChatInputBackgroundColor"),
        ("ComposerStatusWaitingColor", "ChatInputBackgroundColor"),
        ("ComposerStatusReconnectingColor", "ChatInputBackgroundColor"),
        ("ComposerStatusOfflineColor", "ChatInputBackgroundColor"),
    ];

    [Fact]
    public void CriticalTextPairs_MeetWcagAaInLightAndDarkThemes()
    {
        var document = XDocument.Load(PathFor(
            "ConnectOnion.WinUIClient", "Styles", "Colors.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var themeName in new[] { "Light", "Dark" })
        {
            var theme = document.Descendants()
                .Single(element =>
                    element.Name.LocalName == "ResourceDictionary"
                    && element.Attribute(xaml + "Key")?.Value == themeName);
            var colors = theme.Elements()
                .Where(element => element.Name.LocalName == "Color")
                .ToDictionary(
                    element => element.Attribute(xaml + "Key")!.Value,
                    element => Parse(element.Value),
                    StringComparer.Ordinal);

            foreach (var (foregroundKey, backgroundKey) in CriticalPairs)
            {
                Assert.True(colors.TryGetValue(foregroundKey, out var foreground));
                Assert.True(colors.TryGetValue(backgroundKey, out var background));
                var ratio = Contrast(foreground, background);
                Assert.True(
                    ratio >= 4.5,
                    $"{themeName} {foregroundKey} on {backgroundKey} is {ratio:F2}:1; expected at least 4.5:1.");
            }
        }
    }

    private static (byte R, byte G, byte B) Parse(string value)
    {
        var hex = value.Trim().TrimStart('#');
        Assert.True(hex.Length is 6 or 8, $"Unsupported color '{value}'.");
        if (hex.Length == 8) hex = hex[2..];
        return (
            byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double Contrast(
        (byte R, byte G, byte B) foreground,
        (byte R, byte G, byte B) background)
    {
        var lighter = Math.Max(Luminance(foreground), Luminance(background));
        var darker = Math.Min(Luminance(foreground), Luminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance((byte R, byte G, byte B) color)
        => 0.2126 * Linear(color.R)
           + 0.7152 * Linear(color.G)
           + 0.0722 * Linear(color.B);

    private static double Linear(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static string PathFor(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
