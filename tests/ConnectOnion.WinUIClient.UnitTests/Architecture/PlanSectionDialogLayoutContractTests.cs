using System.IO;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Guards the two layout invariants that made the plan section review dialog clip its own content.
///
/// <para>The dialog sizes its content grid in code (<c>ApplyResponsiveSize</c>). A
/// <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/> does <b>not</b> grow to fit a content
/// element that is given an explicit width beyond <c>ContentDialogMaxWidth</c> — whose WinUI
/// default is 548. It renders at its own width and the overflow is clipped on the right, with no
/// scrollbar and no error. Measured before the fix: the section list and every feedback box ran
/// 72epx past the dialog, so each card lost its entire right padding and every section's markdown
/// was cut off mid-sentence.</para>
/// </summary>
public sealed class PlanSectionDialogLayoutContractTests
{
    [Fact]
    public void PlanSectionDialog_ContentWidth_FitsInsideItsOwnDialog()
    {
        var xaml = ReadSource("Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml");
        var code = ReadSource("Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml.cs");

        var dialogMax = ReadDouble(
            Regex.Match(xaml, @"x:Key=""ContentDialogMaxWidth"">\s*([\d.]+)\s*<"),
            "ContentDialogMaxWidth override in PlanSectionReviewDialog.xaml");
        var contentMax = ReadDouble(
            Regex.Match(code, @"MaxContentWidth\s*=\s*([\d.]+)\s*;"),
            "MaxContentWidth in PlanSectionReviewDialog.xaml.cs");
        var chrome = ReadDouble(
            Regex.Match(code, @"DialogChromeWidth\s*=\s*([\d.]+)\s*;"),
            "DialogChromeWidth in PlanSectionReviewDialog.xaml.cs");

        Assert.True(
            contentMax + chrome <= dialogMax,
            $"The content grid can be given {contentMax}epx plus {chrome}epx of dialog chrome, but "
            + $"the dialog is capped at {dialogMax}epx. The difference is clipped silently — raise "
            + "ContentDialogMaxWidth or lower MaxContentWidth.");
    }

    /// <summary>
    /// <c>HorizontalContentAlignment</c> set on a WinUI <c>ListView</c> does not reach the
    /// <c>ListViewItem</c>s it generates. Without the container style each item sizes to its
    /// content's desired width rather than the viewport, so a single wide line — a table, a long
    /// inline code span — pushes that whole card past the right edge of the list.
    /// </summary>
    [Fact]
    public void PlanSectionDialog_ListItems_StretchToTheViewport()
    {
        var xaml = ReadSource("Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml");

        Assert.Contains("<ListView.ItemContainerStyle>", xaml);
        Assert.Matches(
            new Regex(@"TargetType=""ListViewItem""[\s\S]{0,400}?HorizontalContentAlignment""\s+Value=""Stretch"""),
            xaml);
    }

    private static double ReadDouble(Match match, string what)
    {
        Assert.True(match.Success, $"Could not find {what}.");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadSource(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                new[] { dir.FullName, "ConnectOnion.WinUIClient" }.Concat(relativePath).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePath)} from {AppContext.BaseDirectory}");
    }
}
