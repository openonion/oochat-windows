using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class AgentAddressValidatorTests
{
    [Theory]
    [InlineData('0')]
    [InlineData('9')]
    [InlineData('a')]
    [InlineData('f')]
    [InlineData('A')]
    [InlineData('F')]
    public void IsValid_Exactly64HexCharacters_ReturnsTrue(char character)
    {
        Assert.True(AgentAddressValidator.IsValid("0x" + new string(character, 64)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0X1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("0x111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("0x11111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("0x111111111111111111111111111111111111111111111111111111111111111g")]
    public void IsValid_InvalidFormat_ReturnsFalse(string address)
    {
        Assert.False(AgentAddressValidator.IsValid(address));
    }

    [Fact]
    public void ValidationMessage_MatchesFormContract()
    {
        Assert.Equal(
            "Enter a valid agent address (0x + 64 hex characters)",
            AgentAddressValidator.ValidationMessage);
    }
}
