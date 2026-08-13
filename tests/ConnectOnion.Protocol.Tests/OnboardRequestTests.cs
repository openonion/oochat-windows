using System.Text.Json;
using ConnectOnion.Protocol;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// The onboarding trust gate. This frame stands between the user and the agent entirely, so every
/// path here is about degrading into something answerable rather than into nothing.
/// </summary>
public sealed class OnboardRequestTests
{
    private static WireMessage Wrap(string json)
        => WireMessage.Wrap(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ParseOnboard_ReadsMethodsAmountAndAddress()
    {
        var request = AgentInteractiveParsers.ParseOnboard(Wrap("""
            {"type":"ONBOARD_REQUIRED","methods":["invite_code","payment"],
             "payment_amount":5,"payment_address":"0xabc"}
            """));

        Assert.Equal(["invite_code", "payment"], request.Methods);
        Assert.Equal(5, request.PaymentAmount);
        Assert.Equal("0xabc", request.PaymentAddress);
        Assert.True(request.AcceptsInviteCode);
        Assert.True(request.AcceptsPayment);
    }

    [Fact]
    public void ParseOnboard_PaymentOnlyGate_DoesNotOfferAnInviteCode()
    {
        // The case the client could not get past before: an invite-code box for a code that does
        // not exist, and no way to pay.
        var request = AgentInteractiveParsers.ParseOnboard(Wrap("""
            {"methods":["payment"],"payment_amount":2.5}
            """));

        Assert.False(request.AcceptsInviteCode);
        Assert.True(request.AcceptsPayment);
    }

    [Theory]
    // No methods named at all — the overwhelmingly common shape, and the client must still offer
    // the invite code rather than stranding the user on an unanswerable card.
    [InlineData("""{"type":"ONBOARD_REQUIRED"}""")]
    [InlineData("""{"methods":[]}""")]
    // Unreadable methods degrade to the same place.
    [InlineData("""{"methods":"invite_code"}""")]
    [InlineData("""{"methods":[1,2,3]}""")]
    public void ParseOnboard_WithoutUsableMethods_StillOffersTheInviteCode(string json)
    {
        var request = AgentInteractiveParsers.ParseOnboard(Wrap(json));

        Assert.True(request.AcceptsInviteCode);
        Assert.False(request.AcceptsPayment);
    }

    [Theory]
    // A payment gate with no amount is not actionable, so it is not offered as one.
    [InlineData("""{"methods":["payment"]}""")]
    [InlineData("""{"methods":["payment"],"payment_amount":0}""")]
    [InlineData("""{"methods":["payment"],"payment_amount":"5"}""")]
    public void ParseOnboard_PaymentWithoutAnAmount_IsNotOffered(string json)
    {
        Assert.False(AgentInteractiveParsers.ParseOnboard(Wrap(json)).AcceptsPayment);
    }

    [Theory]
    // Never guessed and never defaulted: an invented destination for a real transfer loses money.
    [InlineData("""{"methods":["payment"],"payment_amount":5}""")]
    [InlineData("""{"methods":["payment"],"payment_amount":5,"payment_address":""}""")]
    [InlineData("""{"methods":["payment"],"payment_amount":5,"payment_address":"   "}""")]
    [InlineData("""{"methods":["payment"],"payment_amount":5,"payment_address":123}""")]
    public void ParseOnboard_WithoutAnAddress_ReportsNone(string json)
    {
        Assert.Null(AgentInteractiveParsers.ParseOnboard(Wrap(json)).PaymentAddress);
    }

    [Fact]
    public void ParseOnboard_MethodMatchingIsCaseInsensitive()
    {
        var request = AgentInteractiveParsers.ParseOnboard(Wrap("""
            {"methods":["Invite_Code","PAYMENT"],"payment_amount":1}
            """));

        Assert.True(request.AcceptsInviteCode);
        Assert.True(request.AcceptsPayment);
    }
}
