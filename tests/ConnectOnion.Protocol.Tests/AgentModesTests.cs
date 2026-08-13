using ConnectOnion.Protocol;

namespace ConnectOnion.Protocol.Tests;

public sealed class AgentModesTests
{
    [Theory]
    [InlineData(AgentModes.Safe, AgentModes.AcceptEdits)]
    [InlineData(AgentModes.AcceptEdits, AgentModes.Plan)]
    [InlineData(AgentModes.Plan, AgentModes.Safe)]
    [InlineData("newer-host-mode", AgentModes.Safe)]
    [InlineData(null, AgentModes.Safe)]
    public void Next_CyclesPickerOrderAndFallsBackToSafe(string? current, string expected)
        => Assert.Equal(expected, AgentModes.Next(current));
}
