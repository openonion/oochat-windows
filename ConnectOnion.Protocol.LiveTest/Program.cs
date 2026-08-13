using System;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.Protocol;

// Live end-to-end test against a deployed ConnectOnion agent (Direct URL path).
// Usage: dotnet run -- <directUrl> <address> "<prompt>"
//
// Deliberately outside the .sln: ordinary PR validation must stay deterministic. The CI workflow
// runs this project only through an explicit workflow_dispatch switch, so deployed-agent
// failures remain separated from the commit-level build gate and scheduled CI.
//
// Every interactive turn below is auto-answered so the run completes unattended. That is the
// opposite of what the desktop client does — the point here is to exercise the frames, not the
// decisions — so this file is not a model for how to handle approvals.

// Arguments win locally; CI supplies repository variables without putting endpoint configuration
// in the command line. Defaults retain the known development agent used before CI automation.
var directUrl = Resolve(0, "CONNECTONION_LIVE_DIRECT_URL", "http://124.156.170.117/browser");
var address = Resolve(
    1,
    "CONNECTONION_LIVE_AGENT_ADDRESS",
    "0xf83dfaec8890059e0bc247e78449000a4d81e808ad1d45042cfcbf28406713cb");
var prompt = Resolve(2, "CONNECTONION_LIVE_PROMPT", "Say hello to Alice in one sentence.");

Console.WriteLine($"Direct URL : {directUrl}");
Console.WriteLine($"Address    : {address}");
Console.WriteLine($"Prompt     : {prompt}");

// A throwaway identity per run: this is a smoke test, not a returning client, and generating
// one avoids touching (or needing) the desktop app's stored seed. Note the consequence — the
// target agent sees a brand-new address every run, so it must accept unknown clients, which is
// what the invite code below is for.
var identity = AgentIdentity.Generate();
Console.WriteLine($"Client id  : {identity.ShortAddress}");
Console.WriteLine(new string('-', 60));

await using var connection = new AgentConnectionService(address, directUrl, identity)
{
    InviteCode = ResolveEnvironment("CO_INVITE_CODE", "oochat-smoke-test"),
};
connection.StreamEvent += e => Console.WriteLine($"  «event» {e.Type}: {e.Description}");
connection.ConnectionLost += ex => Console.WriteLine($"  «lost»  {ex.Message}");
connection.AskUserRequested += async r =>
{
    Console.WriteLine($"  «ask_user» {r.Text} (options: {string.Join(", ", r.Options)})");
    await connection.RespondAskUserAsync(r.Options.Count > 0 ? r.Options[0] : "Yes, go ahead.");
    Console.WriteLine("  «ask_user» auto-answered");
};
connection.ApprovalRequested += async r =>
{
    Console.WriteLine($"  «approval» tool={r.Tool} args={r.ArgumentsJson}");
    await connection.RespondApprovalAsync(true, "once");
    Console.WriteLine("  «approval» auto-approved once");
};
connection.PlanReviewRequested += async r =>
{
    Console.WriteLine($"  «plan_review» {r.PlanContent[..Math.Min(80, r.PlanContent.Length)]}…");
    await connection.RespondPlanReviewAsync("Plan approved. Proceed.");
    Console.WriteLine("  «plan_review» auto-approved");
};

var sessionId = Guid.NewGuid().ToString();

try
{
    // Bound the whole attempt so an onboarding gate / hang doesn't run forever.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    Console.WriteLine("Connecting + sending input…");
    var reply = await connection.SendInputAsync(prompt, sessionId, ct: cts.Token);

    Console.WriteLine(new string('-', 60));
    Console.WriteLine("AGENT REPLY:");
    Console.WriteLine(reply);
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"session_id : {connection.SessionId}");
    Console.WriteLine("LIVE TEST PASS");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"LIVE TEST FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

string Resolve(int index, string environmentName, string fallback)
{
    if (args.Length > index && !string.IsNullOrWhiteSpace(args[index])) return args[index];
    return ResolveEnvironment(environmentName, fallback);
}

string ResolveEnvironment(string environmentName, string fallback) =>
    Environment.GetEnvironmentVariable(environmentName) is { Length: > 0 } configured
        ? configured
        : fallback;
