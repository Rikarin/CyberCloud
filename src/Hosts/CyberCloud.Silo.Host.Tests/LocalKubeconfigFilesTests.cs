using CyberCloud.Core;
using Shouldly;
// The same alias the production file needs, and for the same reason: Orleans has an ErrorCode too,
// and a global Orleans using arrives with the silo host's reference set.
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Silo.Host.Tests;

/// <summary>
///     What <c>LocalKubeconfigFiles</c> will and will not read off this machine's disk.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The subject is the root check, not the file read.</b> Reading a file is not what this
///         type is for — <c>File.ReadAllTextAsync</c> would do that in one line. What it is for is
///         deciding whether a <c>CredentialRef</c> is allowed to name the file at all, and that
///         decision is the one place in this host where a value written by a reconciler turns into a
///         path on the host filesystem. Its own remarks say why: <i>"a credential reference that
///         could name any path on the host would make the blast radius of a provider bug the whole
///         filesystem"</i>.
///     </para>
///     <para>
///         ⚠ <b>A rooted path check that is not one is this repository's signature defect wearing a
///         security hat</b>, and it has exactly one classic shape: comparing the prefix without the
///         separator, so a root of <c>/k3s</c> admits <c>/k3s-elsewhere</c>. The production code
///         carries a comment saying so; <see cref="ASiblingDirectoryWhoseNameStartsWithTheRootIsOutsideIt" />
///         is what makes that comment a claim the build checks rather than a claim the build repeats.
///     </para>
///     <para>
///         ⚠ <b>The order of the refusals is asserted, and it is a property rather than an
///         implementation detail.</b> A reference outside the root is refused
///         <see cref="ErrorCode.AuthorizationFailed">AuthorizationFailed</see> whether the file it
///         names exists or not — see
///         <see cref="AReferenceOutsideTheRootIsRefusedWithoutLookingAtTheFilesystem" />. If the
///         existence check ran first, the two refusals would differ, and the difference is a probe:
///         a caller who can write a <c>CredentialRef</c> could ask this host which paths on it exist.
///     </para>
///     <para>
///         The other half of this type's contract — that a silo given no root registers no resolver
///         and keeps <c>KubeApiClientFactory</c>'s refusal — is a claim about a composed host, so it
///         is asserted against the real <c>SiloComposition</c> in
///         <c>CyberCloud.Hosts.Tests</c>'s <c>HostCompositionTests</c>.
///     </para>
/// </remarks>
public sealed class LocalKubeconfigFilesTests : IDisposable {
    /// <summary>A directory that stands in for the AppHost's <c>.k3s</c> mount, per test.</summary>
    readonly string root = Directory
        .CreateDirectory(Path.Combine(Path.GetTempPath(), "cc-kubeconfig-" + Guid.NewGuid().ToString("N")))
        .FullName;

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── The root is a boundary ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The trap the production comment names: a sibling directory sharing the root's name as a
    ///     prefix is not inside the root.
    /// </summary>
    [Fact]
    public async Task ASiblingDirectoryWhoseNameStartsWithTheRootIsOutsideIt() {
        // A real file, really readable, one character outside the boundary.
        var sibling = Directory.CreateDirectory(root + "-elsewhere");

        try {
            var escape = Path.Combine(sibling.FullName, "config");
            await File.WriteAllTextAsync(escape, "apiVersion: v1", TestContext.Current.CancellationToken);

            var error = await RefusalFor(Reference(escape));

            error.Code.ShouldBe(ErrorCode.AuthorizationFailed);
            error.Message.ShouldContain(root);
        } finally {
            sibling.Delete(recursive: true);
        }
    }

    /// <summary>
    ///     A reference that climbs out of the root with <c>..</c> is refused, and the refusal names
    ///     where it landed rather than where it started.
    /// </summary>
    [Fact]
    public async Task AReferenceThatClimbsOutOfTheRootIsRefused() {
        var error = await RefusalFor(Reference(Path.Combine(root, "..", "passwd")));

        error.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        error.Message.ShouldContain(root);
    }

    /// <summary>
    ///     The root check runs before the filesystem is touched, so a refusal cannot be read as an
    ///     answer to "does this path exist?".
    /// </summary>
    /// <remarks>
    ///     ⚠ Both halves are needed. Asserting only the missing path would pass against an
    ///     implementation that checked existence first and happened to reach the root check second.
    /// </remarks>
    [Fact]
    public async Task AReferenceOutsideTheRootIsRefusedWithoutLookingAtTheFilesystem() {
        var outsideAndPresent = Path.Combine(Path.GetTempPath(), "cc-present-" + Guid.NewGuid().ToString("N"));
        var outsideAndAbsent = Path.Combine(Path.GetTempPath(), "cc-absent-" + Guid.NewGuid().ToString("N"));

        await File.WriteAllTextAsync(outsideAndPresent, "apiVersion: v1", TestContext.Current.CancellationToken);

        try {
            var present = await RefusalFor(Reference(outsideAndPresent));
            var absent = await RefusalFor(Reference(outsideAndAbsent));

            present.Code.ShouldBe(ErrorCode.AuthorizationFailed);
            absent.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        } finally {
            File.Delete(outsideAndPresent);
        }
    }

    /// <summary>The root directory is the boundary and not a file, so naming it is refused.</summary>
    [Fact]
    public async Task TheRootItselfIsNotInsideTheRoot() {
        var error = await RefusalFor(Reference(root));

        error.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    /// <summary>
    ///     A root written with a trailing separator is the same root, so the boundary does not move
    ///     with how the AppHost happened to spell its environment variable.
    /// </summary>
    [Fact]
    public async Task ATrailingSeparatorOnTheRootChangesNothing() {
        var config = Path.Combine(root, "config");
        await File.WriteAllTextAsync(config, "apiVersion: v1", TestContext.Current.CancellationToken);

        var resolve = LocalKubeconfigFiles.ResolverFor(root + Path.DirectorySeparatorChar);
        var result = await resolve(Reference(config), TestContext.Current.CancellationToken);

        result.GetValueOrThrow().ShouldBe("apiVersion: v1");
    }

    /// <summary>A relative root is resolved against the process directory, not left relative.</summary>
    /// <remarks>
    ///     ⚠ Without this, a root of <c>.k3s</c> would compare a relative string against an absolute
    ///     path and refuse everything — a silo that silently reaches no cluster, which is the state
    ///     this whole type was written to leave.
    /// </remarks>
    [Fact]
    public async Task ARelativeRootIsResolvedRatherThanComparedAsWritten() {
        var config = Path.Combine(root, "config");
        await File.WriteAllTextAsync(config, "apiVersion: v1", TestContext.Current.CancellationToken);

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        var resolve = LocalKubeconfigFiles.ResolverFor(relative);
        var result = await resolve(Reference(config), TestContext.Current.CancellationToken);

        result.GetValueOrThrow().ShouldBe("apiVersion: v1");
    }

    /// <summary>A root is required, because a resolver with no boundary is the thing being avoided.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AResolverCannotBeBuiltWithoutARoot(string root)
        => Should.Throw<ArgumentException>(() => LocalKubeconfigFiles.ResolverFor(root));

    // ── Inside the root, the answers are distinguishable ──────────────────────────────────────────

    /// <summary>A kubeconfig inside the root is read, bytes for bytes.</summary>
    [Fact]
    public async Task AKubeconfigInsideTheRootIsRead() {
        var config = Path.Combine(root, "k3s.yaml");
        const string contents = "apiVersion: v1\nkind: Config\nclusters: []\n";

        await File.WriteAllTextAsync(config, contents, TestContext.Current.CancellationToken);

        var result = await Resolve(Reference(config));

        result.GetValueOrThrow().ShouldBe(contents);
    }

    /// <summary>A subdirectory of the root is inside the root.</summary>
    [Fact]
    public async Task ASubdirectoryOfTheRootIsInsideIt() {
        var nested = Directory.CreateDirectory(Path.Combine(root, "cluster", "one"));
        var config = Path.Combine(nested.FullName, "kubeconfig");

        await File.WriteAllTextAsync(config, "apiVersion: v1", TestContext.Current.CancellationToken);

        var result = await Resolve(Reference(config));

        result.GetValueOrThrow().ShouldBe("apiVersion: v1");
    }

    /// <summary>
    ///     A kubeconfig that is missing from inside the root is <see cref="ErrorCode.ResourceNotFound" />
    ///     and not an authorization refusal.
    /// </summary>
    /// <remarks>
    ///     ⚠ The distinction is the whole reason this type refuses in sentences. "Not found" and
    ///     "outside the root" are a misplaced file and a misconfigured silo, and the production
    ///     remark says why they must not arrive looking alike: a kubeconfig that cannot be found is
    ///     otherwise indistinguishable, from the reconciler's side, from a cluster that is down.
    /// </remarks>
    [Fact]
    public async Task AMissingKubeconfigInsideTheRootIsNotFoundRatherThanRefused() {
        var error = await RefusalFor(Reference(Path.Combine(root, "absent.yaml")));

        error.Code.ShouldBe(ErrorCode.ResourceNotFound);
        error.Message.ShouldContain("absent.yaml");
    }

    // ── What is not a file reference at all ───────────────────────────────────────────────────────

    /// <summary>A connection carrying no credential reference is a bad request, not a missing file.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AConnectionWithNoCredentialReferenceIsABadRequest(string credentialRef) {
        var error = await RefusalFor(credentialRef);

        error.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    /// <summary>
    ///     A reference in any other scheme is refused by name, and the refusal points at the seam
    ///     that would answer it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>vault:</c> is the one that matters. docs/plan/18's resolver is what replaces this
    ///     type, and until it exists a <c>vault:</c> reference has to fail in a sentence naming
    ///     <c>KubeApiClientFactory.ResolveKubeconfig</c> rather than as "file not found", which is
    ///     what a resolver that stripped the scheme and read the rest would say.
    /// </remarks>
    [Theory]
    [InlineData("vault://secret/data/clusters/one#kubeconfig")]
    [InlineData("https://example.invalid/kubeconfig")]
    [InlineData("k8s-secret://cybercloud/kubeconfig")]
    public async Task AReferenceInAnotherSchemeIsRefusedByName(string credentialRef) {
        var error = await RefusalFor(credentialRef);

        error.Code.ShouldBe(ErrorCode.InternalError);
        error.Message.ShouldContain("ResolveKubeconfig");
    }

    /// <summary>
    ///     A bare filesystem path is not a <c>file:</c> reference, and is refused rather than read.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the one an operator writes by hand, and the tempting fix — "accept a path too"
    ///     — is what would make the scheme check decorative. The path here is a real, readable file
    ///     inside the root, so nothing but the missing scheme can be what refuses it.
    /// </remarks>
    [Fact]
    public async Task ABarePathIsNotACredentialReference() {
        var config = Path.Combine(root, "config");
        await File.WriteAllTextAsync(config, "apiVersion: v1", TestContext.Current.CancellationToken);

        var error = await RefusalFor(config);

        error.Code.ShouldBe(ErrorCode.InternalError);
    }

    /// <summary>Cancellation is observed rather than swallowed into a refusal.</summary>
    /// <remarks>
    ///     ⚠ A silo shutting down mid-reconcile must not record "there is no kubeconfig at …" as the
    ///     reason a cluster could not be reached. The <c>catch</c> arms in the production code name
    ///     <c>IOException</c> and <c>UnauthorizedAccessException</c> and nothing wider, which is what
    ///     lets the cancellation through; this asserts that it stays that way.
    /// </remarks>
    [Fact]
    public async Task CancellationIsNotTurnedIntoARefusal() {
        var config = Path.Combine(root, "config");
        await File.WriteAllTextAsync(config, "apiVersion: v1", TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var resolve = LocalKubeconfigFiles.ResolverFor(root);

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await resolve(Reference(config), cancelled.Token)
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>file:</c> reference a reconciler would write for a path.</summary>
    static string Reference(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>Runs the resolver this test's root produces.</summary>
    Task<Result<string>> Resolve(string credentialRef)
        => LocalKubeconfigFiles.ResolverFor(root)(credentialRef, TestContext.Current.CancellationToken);

    /// <summary>Runs the resolver and insists the answer is a refusal.</summary>
    async Task<Error> RefusalFor(string credentialRef) {
        var result = await Resolve(credentialRef);

        result.TryGetError(out var error).ShouldBeTrue(
            $"'{credentialRef}' was resolved rather than refused."
        );

        return error!;
    }
}
