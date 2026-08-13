using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class SidebarAgentHitTargetTests
{
    [Fact]
    public void AgentButton_SpansTheFullContentHeight_AndVisibleNameDoesNotBlockIt()
    {
        var document = XDocument.Load(FindSidebarXaml());
        var agentButton = document
            .Descendants()
            .Single(element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.AutomationId")?.Value == "AgentButton");

        Assert.Equal("1", agentButton.Attribute("Grid.Column")?.Value);
        Assert.Equal("3", agentButton.Attribute("Grid.ColumnSpan")?.Value);
        Assert.Equal("Stretch", agentButton.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Stretch", agentButton.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Agent_Click", agentButton.Attribute("Click")?.Value);

        var agentName = document
            .Descendants()
            .Single(element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{x:Bind DisplayName}");
        Assert.Equal("False", agentName.Attribute("IsHitTestVisible")?.Value);
    }

    private static string FindSidebarXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(
                directory.FullName,
                "ConnectOnion.WinUIClient",
                "Controls",
                "Shell",
                "ShellSidebar.xaml");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ShellSidebar.xaml.");
    }
}
