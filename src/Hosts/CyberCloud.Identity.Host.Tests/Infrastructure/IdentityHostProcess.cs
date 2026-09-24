using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     The identity host's own executable, started as a separate operating-system process that
///     joins a fixture's cluster as its Orleans client. Every other suite here runs the host
///     inside the test process.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one place a grain call here crosses a process boundary.</b> In the test
///         process the silos, the identity host's Orleans client and the test's own client share one
///         type manifest, so a type that has no <c>[Alias]</c>, or that only one side's manifest
///         allows, still round-trips. The AppHost runs the identity host as a separate process, and
///         that's where #39's shard-map confirmation failed first, after a whole green branch. A
///         call that works through this process works between two processes.
///     </para>
///     <para>
///         The executable is the one the build copies beside the tests, since this project
///         references the host, and it runs the same <c>Program.cs</c> a deployment runs. Nothing
///         is substituted in it. Its password hasher costs what production's does, so a sign-in
///         through it takes about a second longer than one through the fixture's host.
///     </para>
/// </remarks>
public sealed class IdentityHostProcess : IAsyncDisposable {
    readonly Process process;
    readonly ConcurrentQueue<string> output;

    IdentityHostProcess(Process process, ConcurrentQueue<string> output, Uri baseAddress) {
        this.process = process;
        this.output = output;
        BaseAddress = baseAddress;
    }

    /// <summary>Where the process listens, on the loopback interface.</summary>
    public Uri BaseAddress { get; }

    /// <summary>What the process has written to stdout and stderr so far, for a failure message.</summary>
    public string Output => string.Join(Environment.NewLine, output);

    /// <summary>
    ///     Starts the host and waits until its discovery document answers.
    /// </summary>
    /// <param name="settings">
    ///     Command-line settings after the port, the environment and nothing else. The cluster's
    ///     come from <see cref="IdentityHostFixture.ClusterClientSettings" />.
    /// </param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The running process. Dispose it to stop it.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The process exited, or didn't answer within a minute and a half. The message carries its
    ///     output.
    /// </exception>
    public static async Task<IdentityHostProcess> StartAsync(IEnumerable<string> settings, CancellationToken cancellationToken) {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "CyberCloud.Identity.Host" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)
        );
        var baseAddress = new Uri($"http://127.0.0.1:{FreePort()}");

        var start = new ProcessStartInfo(executable) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (var argument in (string[]) ["--environment", "Development", "--urls", baseAddress.GetLeftPart(UriPartial.Authority), .. settings]) {
            start.ArgumentList.Add(argument);
        }

        var output = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = start };

        process.OutputDataReceived += (_, line) => Keep(output, line.Data);
        process.ErrorDataReceived += (_, line) => Keep(output, line.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var started = new IdentityHostProcess(process, output, baseAddress);

        try {
            await started.WaitUntilAnsweringAsync(cancellationToken);
        } catch {
            await started.DisposeAsync();
            throw;
        }

        return started;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (!process.HasExited) {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        process.Dispose();
    }

    async Task WaitUntilAnsweringAsync(CancellationToken cancellationToken) {
        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

        while (DateTime.UtcNow < deadline) {
            if (process.HasExited) {
                throw new InvalidOperationException(
                    $"The identity host exited with {process.ExitCode} before it answered:{Environment.NewLine}{Output}"
                );
            }

            try {
                using var discovery = await http.GetAsync("/.well-known/openid-configuration", cancellationToken);

                if (discovery.StatusCode == HttpStatusCode.OK) {
                    return;
                }
            } catch (HttpRequestException) {
                // Not listening yet.
            } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
                // Listening, and not answering yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new InvalidOperationException(
            $"The identity host didn't answer at {BaseAddress} within 90 seconds:{Environment.NewLine}{Output}"
        );
    }

    /// <summary>Keeps the last lines only: the host logs every request at Information.</summary>
    static void Keep(ConcurrentQueue<string> output, string? line) {
        if (line is null) {
            return;
        }

        output.Enqueue(line);

        while (output.Count > 400 && output.TryDequeue(out _)) { }
    }

    static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
