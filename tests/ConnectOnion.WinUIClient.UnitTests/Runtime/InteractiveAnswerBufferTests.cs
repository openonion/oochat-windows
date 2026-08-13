using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class InteractiveAnswerBufferTests
{
    [Fact]
    public async Task Drain_WaitsForFastTurnPersistenceToReceiveConfirmedAnswer()
    {
        var buffer = new InteractiveAnswerBuffer();
        var reservation = buffer.Begin("conversation", "Answered: Show running agents", EventStatus.Done);

        var drain = buffer.DrainAsync("conversation", TimeSpan.FromSeconds(1));
        Assert.False(drain.IsCompleted);

        buffer.Confirm(reservation);
        var answer = Assert.Single(await drain);
        Assert.Equal("Answered: Show running agents", answer.Meta);
        Assert.Equal(EventStatus.Done, answer.Status);
    }

    [Fact]
    public async Task Drain_SkipsCancelledSendWithoutShiftingLaterAnswer()
    {
        var buffer = new InteractiveAnswerBuffer();
        var failedApproval = buffer.Begin("conversation", "Approved once", EventStatus.Done);
        var ask = buffer.Begin("conversation", "Answered: Logs", EventStatus.Done);
        buffer.Cancel(failedApproval);
        buffer.Confirm(ask);

        var answer = Assert.Single(await buffer.DrainAsync("conversation"));
        Assert.Equal("Answered: Logs", answer.Meta);
    }

    [Fact]
    public async Task Drain_PreservesApprovalThenQuestionFifoOrder()
    {
        var buffer = new InteractiveAnswerBuffer();
        var approval = buffer.Begin("conversation", "Approved once", EventStatus.Done);
        var ask = buffer.Begin("conversation", "Answered: Status", EventStatus.Done);
        buffer.Confirm(ask);
        buffer.Confirm(approval);

        var answers = await buffer.DrainAsync("conversation");
        Assert.Equal(["Approved once", "Answered: Status"], answers.Select(answer => answer.Meta));
    }

    [Fact]
    public async Task Reset_CancelsOutstandingReservations()
    {
        var buffer = new InteractiveAnswerBuffer();
        buffer.Begin("conversation", "Answered: stale", EventStatus.Done);

        buffer.Reset("conversation");

        Assert.Empty(await buffer.DrainAsync("conversation"));
    }
}
