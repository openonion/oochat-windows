using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using ConnectOnion.WinUIClient.Data;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class LayerDependencyTests
{
    private static readonly ArchUnitNET.Domain.Architecture LoadedArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(AppDatabase).Assembly)
        .Build();

    [Fact]
    public void CoreAssembly_DoesNotDependOnWinUiPresentationNamespaces()
    {
        var coreTypes = Types().That()
            .HaveFullNameStartingWith("ConnectOnion.WinUIClient.");
        var presentationTypes = Types().That()
            .HaveFullNameStartingWith("Microsoft.UI.");

        IArchRule rule = Types().That().Are(coreTypes).Should().NotDependOnAny(presentationTypes);
        rule.Check(LoadedArchitecture);
    }

    [Fact]
    public void DataLayer_DoesNotDependOnViewModels()
    {
        var dataTypes = Types().That()
            .HaveFullNameStartingWith("ConnectOnion.WinUIClient.Data.");
        var viewModelTypes = Types().That()
            .HaveFullNameStartingWith("ConnectOnion.WinUIClient.ViewModels.");

        IArchRule rule = Types().That().Are(dataTypes).Should().NotDependOnAny(viewModelTypes);
        rule.Check(LoadedArchitecture);
    }
}
