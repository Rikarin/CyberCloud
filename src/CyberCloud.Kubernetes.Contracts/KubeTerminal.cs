namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     A live attach to one container's terminal: the bytes it prints, the bytes typed into it, and
///     its window size.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An attach, not an exec, and the difference is what "the pod is the shell" means.</b>
///         A console's pod runs <c>/bin/bash -l</c> with <c>stdin</c> and <c>tty</c> as its one
///         process (<c>CloudConsoles.PodJson</c>). Attaching joins <i>that</i> process; an exec would
///         start a second shell beside it, so <c>exit</c> would leave the pod running and a dropped
///         attach would lose the shell's history. With an attach, <c>exit</c> ends the container, the
///         pod ends with it (<c>restartPolicy: Never</c>), and the session is over — which is
///         docs/plan/19's "a console's pod is meant to end".
///     </para>
///     <para>
///         ⚠ <b>Bytes, never text.</b> A terminal stream is escape sequences and partial UTF-8
///         sequences split at arbitrary read boundaries; decoding here would corrupt both.
///     </para>
///     <para>
///         ⚠ <b>No <c>k8s</c> type, because a provider holds one of these.</b> docs/plan/03 § Assembly
///         graph rules, rule 3. The Kubernetes stream protocol stays in <c>CyberCloud.Kubernetes</c>.
///     </para>
/// </remarks>
public interface IKubeTerminal : IAsyncDisposable {
    /// <summary>Reads the next chunk the container printed.</summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Stops the read. The stream stays usable.</param>
    /// <returns>
    ///     How many bytes were read, or <c>0</c> once the stream has ended — the process exited, the
    ///     pod went away, or the API server closed the connection. ⚠ <c>0</c> does not say which:
    ///     read the pod to find out, because "the shell ended" and "the socket dropped under a live
    ///     shell" call for opposite answers.
    /// </returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Sends keystrokes to the container's standard input.</summary>
    /// <param name="input">The bytes typed, unmodified.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);

    /// <summary>Tells the container's terminal its window changed size.</summary>
    /// <param name="columns">The width in cells. At least 1.</param>
    /// <param name="rows">The height in cells. At least 1.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <remarks>
    ///     Carried on the stream's own resize channel, so the kernel's <c>TIOCSWINSZ</c> is set and a
    ///     full-screen program gets its <c>SIGWINCH</c>. <c>stty size</c> inside the container reads
    ///     the result back.
    /// </remarks>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <summary>
///     Opens an <see cref="IKubeTerminal" /> on a cluster by id — the socket half of a cluster
///     connection, which a grain call cannot carry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A seam of its own because an attach is a socket and a grain call is a message.
///         </b> Every other member of <see cref="IKubeClusterConnection" /> is a request and a
///         response, so the production connection forwards it to the one
///         <see cref="IClusterConnectionGrain" /> activation per cluster. An attach is a stream that
///         lives for as long as somebody is typing, and it cannot be returned from a grain method. So
///         the grain <i>authorizes</i> it — its tenancy check runs exactly as for any other call —
///         and hands back how to reach the cluster, and this seam opens the socket in the calling
///         process, which is the process holding the session.
///     </para>
///     <para>
///         ⚠ <b>Here and not in <c>CyberCloud.Kubernetes</c>, for the cycle.</b> The connection a
///         reconciler or a session grain is handed is built in <c>CyberCloud.ResourceManager</c>,
///         which may not reference <c>CyberCloud.Kubernetes</c> (its own remarks say why), so it can
///         only hold the interface; <c>AddCyberCloudKubernetes</c> registers the implementation.
///     </para>
/// </remarks>
public interface IKubeAttachDialer {
    /// <summary>Attaches to a container's terminal.</summary>
    /// <param name="clusterId">The cluster the pod runs in.</param>
    /// <param name="pod">The pod. Its kind must be the core <c>v1</c> <c>Pod</c>.</param>
    /// <param name="container">The container inside it whose process to join.</param>
    /// <param name="cancellationToken">Stops the dial, not the stream once it is open.</param>
    /// <returns>
    ///     The open terminal, or the failure: <see cref="ErrorCode.AuthorizationFailed" /> when the
    ///     caller's tenant does not own the cluster, <see cref="ErrorCode.ResourceNotFound" /> when the
    ///     pod is not there, and the cluster's own refusal otherwise.
    /// </returns>
    Task<Result<IKubeTerminal>> AttachAsync(
        Guid clusterId,
        ObjectRef pod,
        string container,
        CancellationToken cancellationToken = default
    );
}
