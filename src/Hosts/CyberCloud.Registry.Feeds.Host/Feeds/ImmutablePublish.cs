namespace CyberCloud.Registry.Feeds.Host.Feeds;

/// <summary>
///     How an immutable artefact's bytes and its catalogue entry are written so that the entry
///     always describes the bytes the store holds — even when two pushes of the same version race.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             "Ask the catalogue, store the bytes, claim the entry" is safe only for serial pushes,
///             and two CI runners retrying the same publish are not serial.
///         </b> Both pass the catalogue's <c>GetAsync</c>, both <c>PutAsync</c> their bytes, and the
///         grain — which is the one serialisation point — refuses the second claim. If the two wrote
///         to one key, the loser's bytes overwrote the winner's and the surviving entry's
///         <c>Sha256</c>, and npm's <c>shasum</c> and <c>integrity</c>, describe bytes the store no
///         longer holds: every install of that version fails verification, forever, and nothing on
///         the host can tell. The review of #29 found the shape in all three protocols.
///     </para>
///     <para>
///         <b>Two rules close it.</b> First, the bytes of an immutable artefact go under a key that
///         includes their own SHA-256 — <see cref="StoredAt" /> — so two different byte sets never
///         share a key and two identical ones write the same bytes twice, harmlessly. Second, a
///         claim that loses removes the bytes it stored, unless the winner's entry names the very
///         same key, which is the identical-bytes case. The entry's <c>StoredAt</c> is then the
///         only spelling of where the bytes are, and every download reads through it rather than
///         recomputing a path.
///     </para>
///     <para>
///         The replaceable side of Maven — <c>maven-metadata.xml</c>, checksums beside it, anything
///         in a <c>-SNAPSHOT</c> directory — is not routed through here, because "last write wins"
///         is what a replaceable file means and its catalogue entry is never served as a hash.
///     </para>
/// </remarks>
public static class ImmutablePublish {
    /// <summary>
    ///     The key an immutable artefact's bytes go under, relative to the feed's storage prefix:
    ///     the protocol's directory, the bytes' SHA-256 as a segment, then the file's own name.
    /// </summary>
    /// <param name="directory">The protocol's directory for this version, without a trailing slash.</param>
    /// <param name="sha256">The bytes' SHA-256 — <see cref="FeedResponses.Sha256Of" />.</param>
    /// <param name="file">The file name the protocol serves the bytes as.</param>
    public static string StoredAt(string directory, string sha256, string file) => $"{directory}/{sha256}/{file}";

    /// <summary>The directory an entry's bytes live in — everything of <c>StoredAt</c> before the file name.</summary>
    /// <param name="storedAt">A <see cref="FeedEntry.StoredAt" /> this class produced.</param>
    public static string DirectoryOf(string storedAt) {
        ArgumentNullException.ThrowIfNull(storedAt);

        return storedAt[..storedAt.LastIndexOf('/')];
    }

    /// <summary>
    ///     Claims an immutable entry after its bytes were stored, and removes those bytes when the
    ///     claim loses to a push that stored different ones.
    /// </summary>
    /// <param name="context">The feed.</param>
    /// <param name="objects">The store the bytes went into.</param>
    /// <param name="entry">The entry to claim. Its <c>StoredAt</c> is the key the bytes went under.</param>
    /// <param name="storedKeys">
    ///     Every key this push wrote, relative to the feed's prefix — the artefact and anything beside
    ///     it. All are removed when the claim loses to different bytes.
    /// </param>
    /// <param name="cancellationToken">Cancels the delete; the claim itself is a grain call and is not cancelled.</param>
    /// <returns>The claim's result, unchanged — <see cref="ErrorCode.ResourceAlreadyExists" /> when it lost.</returns>
    public static async Task<Result<FeedEntry>> ClaimAsync(
        FeedContext context,
        IObjectStore objects,
        FeedEntry entry,
        IReadOnlyList<string> storedKeys,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(storedKeys);

        var claimed = await context.Catalogue.PutAsync(entry, replace: false);

        if (!claimed.TryGetError(out var refused) || refused.Code != ErrorCode.ResourceAlreadyExists) {
            return claimed;
        }

        // ⚠ The winner's bytes are the winner's; ours are removed only when they are not also the
        // winner's, which the key says: the same bytes hash to the same key.
        var winner = await context.Catalogue.GetAsync(entry.Path);

        if (winner.IsSuccess && winner.GetValueOrThrow().StoredAt == entry.StoredAt) {
            return claimed;
        }

        foreach (var storedKey in storedKeys) {
            await objects.DeleteAsync(context.StoragePrefix + storedKey, cancellationToken);
        }

        return claimed;
    }
}
