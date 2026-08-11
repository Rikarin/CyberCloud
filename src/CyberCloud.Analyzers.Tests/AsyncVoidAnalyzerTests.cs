namespace CyberCloud.Analyzers.Tests;

/// <summary>CC1002 — <c>docs/plan/00 § Coding standards</c>, "No <c>async void</c>".</summary>
public sealed class AsyncVoidAnalyzerTests {
    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AnAsyncVoidMethodIsReported() =>
        AnalyzerHarness.ReportsAsync<AsyncVoidAnalyzer>(
            """
            using System.Threading.Tasks;

            class Worker
            {
                public async void {|CC1002:Start|}()
                {
                    await Task.Yield();
                }
            }
            """
        );

    [Fact]
    public Task AnAsyncVoidLocalFunctionIsReported() =>
        AnalyzerHarness.ReportsAsync<AsyncVoidAnalyzer>(
            """
            using System.Threading.Tasks;

            class Worker
            {
                public void Start()
                {
                    async void {|CC1002:Inner|}()
                    {
                        await Task.Yield();
                    }

                    Inner();
                }
            }
            """
        );

    /// <summary>
    ///     The form that gets written by accident: nothing in the source says "void", the delegate
    ///     type does.
    /// </summary>
    [Fact]
    public Task AnAsyncLambdaBoundToAVoidDelegateIsReported() =>
        AnalyzerHarness.ReportsAsync<AsyncVoidAnalyzer>(
            """
            using System;
            using System.Threading.Tasks;

            class Worker
            {
                public void Start()
                {
                    Action fire = {|CC1002:async|} () => await Task.Yield();
                    fire();
                }
            }
            """
        );

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AnAsyncTaskMethodIsTheWholePoint() =>
        AnalyzerHarness.IsSilentAsync<AsyncVoidAnalyzer>(
            """
            using System.Threading.Tasks;

            class Worker
            {
                public async Task StartAsync() => await Task.Yield();
            }
            """
        );

    /// <summary>
    ///     ⚠ The near-miss that matters. <c>Task.Run(async () =&gt; …)</c> is correct and everywhere;
    ///     a rule that looked at the lambda's <c>async</c> keyword rather than at the delegate it
    ///     binds to would fire on all of it.
    /// </summary>
    [Fact]
    public Task AnAsyncLambdaBoundToFuncOfTaskIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<AsyncVoidAnalyzer>(
            """
            using System.Threading.Tasks;

            class Worker
            {
                public Task StartAsync() => Task.Run(async () => await Task.Yield());
            }
            """
        );

    [Fact]
    public Task AnAsyncLambdaBoundToFuncOfValueTaskIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<AsyncVoidAnalyzer>(
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            class Worker
            {
                public void Start()
                {
                    Func<int, CancellationToken, ValueTask> body =
                        async (item, token) => await Task.Yield();
                    _ = body;
                }
            }
            """
        );

    /// <summary>A plain <c>void</c> method is not this rule's business.</summary>
    [Fact]
    public Task ASynchronousVoidMethodIsFine() =>
        AnalyzerHarness.IsSilentAsync<AsyncVoidAnalyzer>(
            """
            class Worker
            {
                public void Start()
                {
                }
            }
            """
        );
}
