namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1001 — <c>docs/plan/00 § Coding standards</c>, the half of the <c>.Result</c> / <c>.Wait()</c>
///     ban that <c>CA1849</c> does not cover.
/// </summary>
public sealed class BlockingWaitAnalyzerTests
{
    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task ResultFromASynchronousMethodIsReported() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public string Read(Task<string> pending) => {|CC1001:pending.Result|};
            }
            """);

    [Fact]
    public Task WaitFromASynchronousMethodIsReported() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public void Drain(Task pending)
                {
                    {|CC1001:pending.Wait()|};
                }
            }
            """);

    [Fact]
    public Task GetAwaiterGetResultFromASynchronousMethodIsReported() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public string Read(Task<string> pending) => {|CC1001:pending.GetAwaiter().GetResult()|};
            }
            """);

    /// <summary>The evasion that would otherwise make the rule decorative.</summary>
    [Fact]
    public Task ConfigureAwaitDoesNotHideTheBlock() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public string Read(Task<string> pending) =>
                    {|CC1001:pending.ConfigureAwait(false).GetAwaiter().GetResult()|};
            }
            """);

    [Fact]
    public Task AConstructorCountsAsASynchronousMethod() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                readonly string value;

                public Caller(Task<string> pending)
                {
                    value = {|CC1001:pending.Result|};
                }

                public string Value => value;
            }
            """);

    // ── negative — every one of these is correct code ────────────────────────────────────────────

    /// <summary>
    ///     CA1849's territory, and it is already an error in <c>.editorconfig</c>. Two ids on one
    ///     line teaches people to suppress both.
    /// </summary>
    [Fact]
    public Task InsideAnAsyncMethodItIsCa1849sJob() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public async Task<string> ReadAsync(Task<string> pending)
                {
                    await Task.Yield();
                    return pending.Result;
                }
            }
            """);

    [Fact]
    public Task AwaitingIsObviouslyFine() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public async Task<string> ReadAsync(Task<string> pending) => await pending;
            }
            """);

    /// <summary>
    ///     ⚠ <c>CyberCloud.Core.Result&lt;T&gt;</c> exists. A rule that matched the member name
    ///     alone would fire on every one of this repository's own domain results.
    /// </summary>
    [Fact]
    public Task APropertyCalledResultOnSomethingThatIsNotATaskIsNotBlocking() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            sealed class Parsed
            {
                public string Result => "parsed";
            }

            class Caller
            {
                public string Read(Parsed parsed) => parsed.Result;
            }
            """);

    /// <summary>A blocking primitive is not a blocked task, and banning it is not this rule's job.</summary>
    [Fact]
    public Task SemaphoreSlimWaitIsNotTaskWait() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading;

            class Caller
            {
                public void Enter(SemaphoreSlim gate) => gate.Wait();
            }
            """);

    /// <summary>
    ///     Exemption 1. <c>CyberCloud.ServiceDefaults.Storage.HotTierConfigurator</c>'s constructor
    ///     is exactly this: inside a continuation the antecedent has completed, and
    ///     <c>GetAwaiter().GetResult()</c> is how you re-raise its exception unwrapped.
    /// </summary>
    [Fact]
    public Task TheContinueWithAntecedentHasAlreadyCompleted() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading;
            using System.Threading.Tasks;

            class Caller
            {
                public Task<object> Unwrap(Task<string> pending) =>
                    pending.ContinueWith(
                        finished => (object)finished.GetAwaiter().GetResult(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
            }
            """);

    /// <summary>
    ///     Exemption 2. <c>HotTierConfigurator.Dispose</c> is exactly this: a task whose completion
    ///     was established by the enclosing <c>if</c> cannot block when it is read.
    /// </summary>
    [Fact]
    public Task AReadGuardedByIsCompletedSuccessfullyCannotBlock() =>
        AnalyzerHarness.IsSilentAsync<BlockingWaitAnalyzer>(
            """
            using System;
            using System.Threading.Tasks;

            class Caller
            {
                readonly Lazy<Task<IDisposable>> resource;

                public Caller(Lazy<Task<IDisposable>> resource) => this.resource = resource;

                public void Dispose()
                {
                    if (resource.IsValueCreated && resource.Value.IsCompletedSuccessfully)
                    {
                        resource.Value.Result.Dispose();
                    }
                }
            }
            """);

    /// <summary>
    ///     ⚠ The exemption is scoped to the guarded expression, not to the method. A <i>different</i>
    ///     task read inside the same <c>if</c> still blocks, and is still reported.
    /// </summary>
    [Fact]
    public Task TheCompletionGuardDoesNotCoverADifferentTask() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System.Threading.Tasks;

            class Caller
            {
                public string Read(Task<string> guarded, Task<string> other)
                {
                    if (guarded.IsCompletedSuccessfully)
                    {
                        return {|CC1001:other.Result|};
                    }

                    return "";
                }
            }
            """);

    /// <summary>
    ///     A synchronous lambda inside an <c>async</c> method is still a synchronous context — the
    ///     enclosing function is what matters, not the enclosing member.
    /// </summary>
    [Fact]
    public Task ASynchronousLambdaInsideAnAsyncMethodIsStillSynchronous() =>
        AnalyzerHarness.ReportsAsync<BlockingWaitAnalyzer>(
            """
            using System;
            using System.Threading.Tasks;

            class Caller
            {
                public async Task RunAsync(Task<string> pending)
                {
                    Func<string> read = () => {|CC1001:pending.Result|};
                    await Task.Yield();
                    read();
                }
            }
            """);
}
