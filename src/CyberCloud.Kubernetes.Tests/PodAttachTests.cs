using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;
using System.Text;
using k8s;
using k8s.Models;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The <c>pods/attach</c> stream the cloud terminal's session grain speaks — against a real
///     kubelet, through <c>KubeApiClient.AttachAsync</c> and nothing else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A real kubelet, because every property here is the kubelet's.</b> Whether a resize
///         reaches the terminal is decided by the kubelet reading the resize channel; whether an empty
///         frame opens a channel, and whether the stream ends when the process does, are the API
///         server's. A fake would assert the protocol this file's author believed in — and one belief
///         did not survive: lower-case resize keys were expected to be ignored, and Go's decoder
///         matches them anyway. Sabotage-verified instead by never sending the resize, which leaves
///         <c>stty size</c> at the attach's default and fails the second wait.
///     </para>
///     <para>
///         The shell image is the durable tier's own PostgreSQL image, by digest: it carries
///         <c>bash</c>, BusyBox's <c>stty</c> and <c>psql</c>, and every cluster-backed run on this
///         machine has already pulled it for the harness's database.
///     </para>
/// </remarks>
[Collection(K3sSuite.Name)]
public sealed class PodAttachTests(K3sFixture k3s) {
    /// <summary>A small image with bash and stty, pinned by digest.</summary>
    public const string ShellImage =
        "docker.io/library/postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24";

    static readonly GroupVersionKind Pods = new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" };

    [Fact]
    public async Task KeystrokesRoundTripAResizeReachesTheTerminalAndExitEndsTheStream() {
        var pod = await RunningShellAsync("attach-echo");

        var attached = await k3s.Api.AttachAsync(pod, "shell", TestContext.Current.CancellationToken);
        attached.IsSuccess.ShouldBeTrue(attached.Error?.Message);

        await using var terminal = attached.GetValueOrThrow();
        var screen = new StringBuilder();

        await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);

        // ⚠ The expected text is not in the command: `cc-$((6*7))` is echoed back by the terminal as
        // typed, and only the shell's arithmetic prints `cc-42`. A test that looked for the command's
        // own text would pass on a terminal that echoed and never ran anything.
        var token = TestContext.Current.CancellationToken;

        await terminal.WriteAsync("echo cc-$((6*7))\r"u8.ToArray(), token);
        await ReadUntilAsync(terminal, screen, "cc-42");

        await terminal.ResizeAsync(132, 43, token);
        await terminal.WriteAsync("stty size\r"u8.ToArray(), token);
        await ReadUntilAsync(terminal, screen, "43 132");

        await terminal.WriteAsync("exit\r"u8.ToArray(), token);

        var buffer = new byte[4096];
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (await terminal.ReadAsync(buffer, bounded.Token) > 0) {
            // Drain what the shell printed on its way out.
        }

        // The stream ended because the process did — and the pod with it, restartPolicy: Never.
        var phase = await PhaseAsync(pod.Name);
        phase.ShouldBeOneOf("Succeeded", "Running");
    }

    [Fact]
    public async Task AnAttachToAPodThatIsNotThereIsARefusalNotAThrow() {
        var attached = await k3s.Api.AttachAsync(
            new() { Kind = Pods, Namespace = K3sFixture.Namespace, Name = "no-such-shell" },
            "shell",
            TestContext.Current.CancellationToken
        );

        attached.IsSuccess.ShouldBeFalse();
        attached.Error!.Message.ShouldNotBeNullOrWhiteSpace();
    }

    async Task<ObjectRef> RunningShellAsync(string name) {
        var token = TestContext.Current.CancellationToken;

        await k3s.Raw.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                Metadata = new() { Name = name },
                Spec = new() {
                    RestartPolicy = "Never",
                    TerminationGracePeriodSeconds = 1,
                    Containers = [
                        new() {
                            Name = "shell",
                            Image = ShellImage,
                            Command = ["/bin/bash", "-l"],
                            Stdin = true,
                            Tty = true
                        }
                    ]
                }
            },
            K3sFixture.Namespace,
            cancellationToken: token
        );

        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);

        while (await PhaseAsync(name) != "Running") {
            if (DateTimeOffset.UtcNow > deadline) {
                throw new TimeoutException($"pod {name} did not reach Running: it is {await PhaseAsync(name)}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        return new() { Kind = Pods, Namespace = K3sFixture.Namespace, Name = name };
    }

    async Task<string> PhaseAsync(string name) {
        var pod = await k3s.Raw.CoreV1.ReadNamespacedPodAsync(
            name,
            K3sFixture.Namespace,
            cancellationToken: TestContext.Current.CancellationToken
        );

        return pod.Status?.Phase ?? "Pending";
    }

    /// <summary>Reads until the screen shows <paramref name="expected" />, or fails with what it did show.</summary>
    static async Task ReadUntilAsync(IKubeTerminal terminal, StringBuilder screen, string expected) {
        var buffer = new byte[4096];
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try {
            while (!screen.ToString().Contains(expected, StringComparison.Ordinal)) {
                var read = await terminal.ReadAsync(buffer, bounded.Token);
                read.ShouldBeGreaterThan(0, $"the stream ended before '{expected}' appeared. Screen: {screen}");
                screen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        } catch (OperationCanceledException) {
            throw new TimeoutException($"'{expected}' never appeared. Screen so far: {screen}");
        }
    }
}
