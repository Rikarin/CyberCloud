using CyberCloud.Kubernetes.Contracts;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CyberCloud.ResourceManager.Tests.Infrastructure;

/// <summary>
///     One shell pod in one cluster, scripted, for the terminal session grain to attach to.
/// </summary>
/// <remarks>
///     ⚠ <b>Static, like the rest of this harness's doubles</b>: the silo builds the connection
///     factory in its own container, and the test has no other handle on it. Only
///     <see cref="ClusterId" /> gets a connection, so every other resource in the suite keeps the
///     refusing default it had before this existed.
/// </remarks>
public static class ScriptedShell {
    /// <summary>The one cluster with a connection.</summary>
    public static readonly Guid ClusterId = Guid.Parse("5e55e55e-0000-4000-8000-000000000022");

    /// <summary>The pod the sessions attach to.</summary>
    public static ObjectRef Pod { get; } = new() {
        Kind = new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" },
        Namespace = "cc-shell",
        Name = "shell"
    };

    /// <summary>The pod's UID, which is the session id a test registers.</summary>
    public static string PodUid { get; set; } = string.Empty;

    /// <summary>The pod's <c>status.phase</c>.</summary>
    public static string Phase { get; set; } = "Running";

    /// <summary>Answers for the next attaches, in order; an empty queue opens a terminal.</summary>
    public static ConcurrentQueue<Result<IKubeTerminal>> Attaches { get; } = new();

    /// <summary>Every terminal an attach opened, in order.</summary>
    public static ConcurrentQueue<ScriptedTerminal> Opened { get; } = new();

    /// <summary>How many attaches were asked for.</summary>
    public static int AttachCalls => attachCalls;

    /// <summary>Every pod name deleted, in order.</summary>
    public static ConcurrentQueue<string> Deleted { get; } = new();

    /// <summary>Whether a terminal ends its stream as soon as it's opened — a stream that keeps dropping.</summary>
    public static bool DropOnOpen { get; set; }

    /// <summary>How many of the next deletes the cluster refuses as a retry, the way a degraded connection does.</summary>
    public static int RefuseDeletes { get; set; }

    static int attachCalls;

    /// <summary>A fresh pod, running, with a new UID; returns the UID.</summary>
    public static string Reset() {
        PodUid = Guid.NewGuid().ToString("D");
        Phase = "Running";
        Attaches.Clear();
        Opened.Clear();
        Deleted.Clear();
        DropOnOpen = false;
        RefuseDeletes = 0;
        Interlocked.Exchange(ref attachCalls, 0);
        return PodUid;
    }

    /// <summary>A spec for the scripted pod in a tenant.</summary>
    /// <param name="tenant">The tenant the console and the session belong to.</param>
    public static TerminalSessionSpec Spec(Guid tenant) =>
        new() {
            Resource = new(tenant, ResourceManagerCluster.Subscription, "prod", ConformingReconciler.TypeName, "console", Guid.NewGuid()),
            ApiVersion = TestingProvider.V2026,
            ClusterId = ClusterId,
            Pod = Pod,
            Container = "shell",
            PodUid = PodUid,
            IdleTimeoutSeconds = 1200,
            Permission = "connect",
            ReadPermission = "read"
        };

    internal static Result<IKubeTerminal> NextAttach() {
        Interlocked.Increment(ref attachCalls);

        if (Attaches.TryDequeue(out var scripted)) {
            return scripted;
        }

        var terminal = new ScriptedTerminal();
        Opened.Enqueue(terminal);

        if (DropOnOpen) {
            terminal.Close();
        }

        return Result<IKubeTerminal>.Success(terminal);
    }
}

/// <summary>The silo's connection factory: a connection for <see cref="ScriptedShell.ClusterId" /> and nothing else.</summary>
public sealed class ScriptedShellClusters : IClusterConnectionFactory {
    /// <inheritdoc />
    public IKubeClusterConnection? Connect(Guid clusterId) =>
        clusterId == ScriptedShell.ClusterId ? new ScriptedShellConnection() : null;

    sealed class ScriptedShellConnection : IKubeClusterConnection {
        public Guid ClusterId => ScriptedShell.ClusterId;

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ApplyOutcome>.Failure(ErrorCode.InternalError, "the scripted shell applies nothing."));

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
            if (ScriptedShell.Deleted.Contains(target.Name)) {
                return Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here."));
            }

            var pod = new JsonObject {
                ["metadata"] = new JsonObject { ["name"] = target.Name, ["uid"] = ScriptedShell.PodUid },
                ["status"] = new JsonObject { ["phase"] = ScriptedShell.Phase }
            };

            return Task.FromResult(Result<KubeObject>.Success(new() { Ref = target, Json = pod.ToJsonString() }));
        }

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            if (ScriptedShell.RefuseDeletes > 0) {
                ScriptedShell.RefuseDeletes--;
                return Task.FromResult(Result.Failure(ErrorCode.OperationInProgress, "the cluster has not answered lately."));
            }

            ScriptedShell.Deleted.Enqueue(command.Target.Name);
            return Task.FromResult(Result.Success);
        }

        public Task<Result<IKubeTerminal>> AttachAsync(
            ObjectRef pod,
            string container,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(ScriptedShell.NextAttach());
    }
}

/// <summary>A terminal whose output a test writes and whose input a test reads.</summary>
public sealed class ScriptedTerminal : IKubeTerminal {
    readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();

    /// <summary>Everything the session sent, as text.</summary>
    public ConcurrentQueue<string> Written { get; } = new();

    /// <summary>Every resize, in order.</summary>
    public ConcurrentQueue<(int Columns, int Rows)> Resizes { get; } = new();

    /// <summary>Makes the shell print something.</summary>
    /// <param name="text">What it prints.</param>
    public void Print(string text) => output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>Ends the stream, as a shell that exits or a dropped socket does.</summary>
    public void Close() => output.Writer.TryComplete();

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        if (!await output.Reader.WaitToReadAsync(cancellationToken) || !output.Reader.TryRead(out var chunk)) {
            return 0;
        }

        chunk.CopyTo(buffer);
        return chunk.Length;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) {
        Written.Enqueue(Encoding.UTF8.GetString(input.Span));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) {
        Resizes.Enqueue((columns, rows));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A pane, as the session grain sees one: what it was shown and whether it was told the end.</summary>
public sealed class RecordingViewer : ITerminalViewer {
    readonly StringBuilder screen = new();

    /// <summary>Everything delivered, as text.</summary>
    public string Screen {
        get {
            lock (screen) {
                return screen.ToString();
            }
        }
    }

    /// <summary>The reason the session gave for ending, or <see langword="null" />.</summary>
    public string? Ended { get; private set; }

    /// <inheritdoc />
    public Task OutputAsync(byte[] data) {
        lock (screen) {
            screen.Append(Encoding.UTF8.GetString(data));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EndedAsync(string reason) {
        Ended = reason;
        return Task.CompletedTask;
    }
}
