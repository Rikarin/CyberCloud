namespace CyberCloud.Providers.Terminal;

/// <summary>
///     The shell image a deployment runs, per variant — <c>CyberCloud:Terminal:Images</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A deployment input, like every other image digest this platform runs.</b> The host
///         images' digests are printed by <c>./build.sh Images</c> and handed to
///         <c>deploy/bootstrap/bootstrap.sh --image</c>; nothing checks them in, because a digest
///         changes on every build. The shell image is the same kind of fact and arrives the same way:
///         a value in the gateway's and the silo's configuration, set by whoever deployed.
///         <c>CloudConsoles.ImageDigests</c>' visible placeholders remain the answer when nothing is
///         configured, so an unconfigured platform fails to pull loudly rather than running something.
///     </para>
///     <para>
///         ⚠ <b>Digest-pinned or refused.</b> A configured value that is not
///         <c>…@sha256:&lt;64 hex&gt;</c> makes <c>connect</c> refuse with a message naming the setting —
///         see <see cref="CloudConsoles.IsPinned" />.
///     </para>
/// </remarks>
public sealed class CloudShellImageOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Terminal:Images";

    /// <summary>The <c>default</c> variant's image, by digest. Empty means the placeholder.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>The <c>minimal</c> variant's image, by digest. Empty falls back to <see cref="Default" />.</summary>
    /// <remarks>
    ///     ⚠ Falls back rather than to the placeholder: <c>conformance.yaml § owed</c>,
    ///     <c>the-minimal-variant-is-a-second-image-nobody-costed</c>, says a pipeline with one image
    ///     must resolve <c>minimal</c> to the same digest as <c>default</c> rather than to nothing.
    /// </remarks>
    public string Minimal { get; set; } = string.Empty;

    /// <summary>The image for a body's variant: the configured one, or the placeholder when none is.</summary>
    /// <param name="variant">The body's <c>/properties/image/variant</c>.</param>
    /// <param name="placeholder">What the contracts say when nothing is configured.</param>
    public string For(string variant, string placeholder) {
        var configured = string.Equals(variant, "minimal", StringComparison.Ordinal) && Minimal.Length > 0
            ? Minimal
            : Default;

        return configured.Length > 0 ? configured : placeholder;
    }
}
