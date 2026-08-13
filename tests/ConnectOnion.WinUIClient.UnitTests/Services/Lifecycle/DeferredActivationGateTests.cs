using ConnectOnion.WinUIClient.Services.Lifecycle;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Lifecycle;

public sealed class DeferredActivationGateTests
{
    [Fact]
    public void RequestsBeforeAttach_CoalesceAndReplayExactlyOnce()
    {
        var gate = new DeferredActivationGate();
        var activations = 0;

        gate.Request();
        gate.Request();
        gate.Request();
        gate.Attach(() => activations++);

        Assert.Equal(1, activations);
    }

    [Fact]
    public void RequestsAfterAttach_RunImmediately()
    {
        var gate = new DeferredActivationGate();
        var activations = 0;
        gate.Attach(() => activations++);

        gate.Request();
        gate.Request();

        Assert.Equal(2, activations);
    }

    [Fact]
    public void RequestAfterDetach_WaitsForTheNextWindow()
    {
        var gate = new DeferredActivationGate();
        var firstWindow = 0;
        var secondWindow = 0;
        gate.Attach(() => firstWindow++);
        gate.Detach();

        gate.Request();
        Assert.Equal(0, firstWindow);

        gate.Attach(() => secondWindow++);
        Assert.Equal(1, secondWindow);
    }

    [Fact]
    public async Task RequestRacingAttach_NeverLosesTheActivation()
    {
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var gate = new DeferredActivationGate();
            var activations = 0;
            using var start = new ManualResetEventSlim();

            var request = Task.Run(() =>
            {
                start.Wait();
                gate.Request();
            });
            var attach = Task.Run(() =>
            {
                start.Wait();
                gate.Attach(() => Interlocked.Increment(ref activations));
            });

            start.Set();
            await Task.WhenAll(request, attach);

            Assert.Equal(1, Volatile.Read(ref activations));
        }
    }
}
