using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CyberCloud.Cli.Extensions;

/// <summary>
///     The extensions this machine has installed — where they live, what they hashed to when they were
///     installed, and whether the directory holding them can be trusted.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE TRUST BOUNDARY IS <c>~/.cyc/extensions</c>, AND <c>PATH</c> IS NEVER CONSULTED.</b>
///         docs/plan/21 § Extensions states it and this type is where it is enforced. <c>git</c> and
///         <c>kubectl</c> run anything named <c>git-foo</c> or <c>kubectl-foo</c> that turns up on
///         <c>PATH</c>. That convention is cheap for a version-control client and expensive here,
///         because <c>cyc</c> holds a cloud credential: under it, the set of programs that can spend
///         the user's cloud budget equals the set of programs in <i>any</i> writable directory on
///         <c>PATH</c> — a set the user never chose and cannot enumerate. <c>cyc</c> runs only what
///         <c>cyc extension add</c> put here, which is <c>gh</c>'s model.
///     </para>
///     <para>
///         ⚠ <b>What that costs, said out loud rather than glossed.</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>No zero-install extensions.</b> Dropping <c>cyc-foo</c> on <c>PATH</c> does nothing
///             at all. Every extension is an explicit <c>cyc extension add</c>, and a distribution
///             that wants one installed has to say so.
///         </item>
///         <item>
///             <b>Install is the trust decision, and there is no second one.</b> An installed
///             extension is an ordinary child process running as the user: it can read the keychain by
///             running <c>cyc account get-access-token</c>, exactly as the user can. Nothing here
///             sandboxes, and neither does <c>gh</c>, <c>krew</c> or <c>az</c>.
///             <c>cyc extension add</c> says so once, at the moment the decision is being made.
///         </item>
///         <item>
///             <b>The recorded hash catches an accident, not an adversary.</b>
///             <see cref="ExtensionRecord.Sha256" /> is verified on every invocation, which catches a
///             binary replaced without the index being rewritten — a package manager overwriting the
///             file, a half-finished manual copy, a truncated download. It does <i>not</i> stop
///             somebody who can write the directory, because they can write
///             <see cref="IndexFileName" /> in the same breath. Against that attacker the permission
///             check below is the only thing that helps.
///         </item>
///         <item>
///             <b>Permissions are checked on Unix and not on Windows.</b>
///             <see cref="UnsafeDirectories" /> refuses a group- or world-writable <c>~/.cyc</c> or
///             <c>~/.cyc/extensions</c>, because a shared-writable install directory is the <c>PATH</c>
///             model wearing a different hat. <see cref="FileSystemInfo.UnixFileMode" /> says nothing
///             about a Windows ACL, so on Windows the model rests on the profile directory's default
///             ACL and this check does not run. An unstated gap is worse than a known one.
///         </item>
///     </list>
/// </remarks>
sealed class ExtensionStore {
    /// <summary>
    ///     The prefix an extension's file carries — <c>cyc-shell</c> for <c>cyc shell</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Kept even though <c>PATH</c> is not searched, so one artifact serves both worlds: a
    ///     project can ship <c>cyc-foo</c>, install it here, and the same file still reads as an
    ///     extension to anyone who inspects the directory.
    /// </remarks>
    public const string FilePrefix = "cyc-";

    /// <summary>The index, beside the executables it describes.</summary>
    public const string IndexFileName = "index.json";

    /// <summary>The format version the index carries, so a future shape can be told apart from a corrupt file.</summary>
    public const string SupportedFormat = "1";

    readonly List<ExtensionRecord> records;

    ExtensionStore(string directory, List<ExtensionRecord> records) {
        Directory = directory;
        this.records = records;
    }

    /// <summary>Where the executables and the index live.</summary>
    public string Directory { get; }

    /// <summary>The index file.</summary>
    public string IndexPath => Path.Combine(Directory, IndexFileName);

    /// <summary>Every installed extension, in the order they were installed.</summary>
    public IReadOnlyList<ExtensionRecord> Records => records;

    /// <summary>The extensions directory for a host.</summary>
    /// <param name="host">The host, whose <see cref="CycHost.StateDirectory" /> is <c>~/.cyc</c>.</param>
    public static string DirectoryFor(CycHost host) {
        ArgumentNullException.ThrowIfNull(host);

        return Path.Combine(host.StateDirectory, "extensions");
    }

    /// <summary>
    ///     Reads the index.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <returns>The store. An absent directory or index is an empty store, which is what a machine with no extensions has.</returns>
    /// <exception cref="CycClientException">
    ///     ⚠ The index exists and does not parse, or carries a format this build does not read. This
    ///     is deliberately <i>not</i> degraded to "no extensions installed": a corrupt index that
    ///     reports success is how an installed extension disappears without anybody noticing, and the
    ///     resulting "unrecognized command" would send the reader looking in entirely the wrong place.
    /// </exception>
    public static ExtensionStore Open(CycHost host) {
        var directory = DirectoryFor(host);
        var path = Path.Combine(directory, IndexFileName);

        if (!File.Exists(path))
            return new ExtensionStore(directory, []);

        string text;

        try {
            text = File.ReadAllText(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new CycClientException($"'{path}' could not be read: {e.Message}", e);
        }

        ExtensionIndex? index;

        try {
            index = JsonSerializer.Deserialize(text, ExtensionJsonContext.Default.ExtensionIndex);
        } catch (JsonException e) {
            throw new CycClientException(
                $"'{path}' is not readable JSON, so cyc cannot tell which extensions are installed: {e.Message} "
                + "Fix or delete the file, then reinstall with 'cyc extension add'.",
                e);
        }

        if (index is null)
            throw new CycClientException($"'{path}' is empty. Delete it, then reinstall with 'cyc extension add'.");

        if (!string.Equals(index.Format, SupportedFormat, StringComparison.Ordinal))
            throw new CycClientException(
                $"'{path}' is format '{index.Format}' and this build of cyc reads format '{SupportedFormat}'. Upgrade cyc.");

        return new ExtensionStore(directory, [.. index.Extensions]);
    }

    /// <summary>One installed extension, or <c>null</c>.</summary>
    /// <param name="name">The verb, without the <see cref="FilePrefix" />.</param>
    public ExtensionRecord? Find(string name)
        => records.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where an extension's executable is, installed or not.</summary>
    /// <param name="name">The verb.</param>
    public string PathFor(string name) => Path.Combine(Directory, FilePrefix + name);

    /// <summary>
    ///     The executables sitting in the directory that no index entry claims.
    /// </summary>
    /// <remarks>
    ///     ⚠ These never run, and saying nothing about them would be the defect. A file copied into
    ///     the install directory by hand looks installed to whoever copied it; leaving
    ///     <c>cyc foo</c> to answer "unrecognized command" would send them hunting through their
    ///     <c>PATH</c> for a mechanism this CLI does not have.
    /// </remarks>
    public IReadOnlyList<string> Unregistered() {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        var claimed = records.Select(x => FilePrefix + x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [
            .. System.IO.Directory
                .EnumerateFiles(Directory, FilePrefix + "*")
                .Select(Path.GetFileName)
                .Where(x => x is not null && x.Length > FilePrefix.Length && !claimed.Contains(x))
                .Select(x => x![FilePrefix.Length..])
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    ///     The directories between <c>~/.cyc</c> and the executables that anyone but the owner can
    ///     write to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A shared-writable install directory is the <c>PATH</c> model with extra steps.</b>
    ///         The whole reason this CLI does not search <c>PATH</c> is that a writable directory on
    ///         it becomes arbitrary code execution under the user's cloud credentials; a group- or
    ///         world-writable <c>~/.cyc/extensions</c> is exactly the same hole.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two levels, not a walk to the root.</b> Somebody who can write <c>~</c> can
    ///         replace <c>~/.cyc</c> wholesale, and no check inside <c>~/.cyc</c> survives that. The
    ///         line is drawn where it is cheap and honest rather than pretending to a guarantee the
    ///         home directory does not give.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty on Windows</b>, because <see cref="FileSystemInfo.UnixFileMode" /> reports
    ///         nothing about an ACL. Documented in this type's remarks rather than silently skipped.
    ///     </para>
    /// </remarks>
    /// <param name="host">The host.</param>
    public static IReadOnlyList<string> UnsafeDirectories(CycHost host) {
        ArgumentNullException.ThrowIfNull(host);

        if (OperatingSystem.IsWindows())
            return [];

        return [.. new[] { host.StateDirectory, DirectoryFor(host) }.Where(IsSharedWritable)];
    }

    static bool IsSharedWritable(string directory) {
        var info = new DirectoryInfo(directory);

        if (!info.Exists)
            return false;

        return (info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
    }

    /// <summary>
    ///     Copies an executable in, records what it hashed to, and returns the new store.
    /// </summary>
    /// <param name="name">The verb it answers to. Already checked by <see cref="IsLegalName" />.</param>
    /// <param name="source">The file to copy. Read once, hashed from the copy rather than from the original.</param>
    /// <remarks>
    ///     ⚠ The file is chmod 0700, which both makes it runnable and takes it out of reach of anyone
    ///     but its owner. The hash is taken <i>after</i> the copy so that what is recorded is what will
    ///     be run, not what was read.
    /// </remarks>
    public ExtensionStore Install(string name, string source) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(source);

        System.IO.Directory.CreateDirectory(Directory);

        var destination = PathFor(name);

        File.Copy(source, destination, overwrite: true);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var record = new ExtensionRecord {
            Name = name,
            Sha256 = HashOf(destination),
            Size = new FileInfo(destination).Length,
            Source = Path.GetFullPath(source),
            Installed = DateTimeOffset.UtcNow,
        };

        var kept = records.Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        kept.Add(record);

        var store = new ExtensionStore(Directory, kept);

        store.WriteIndex();

        return store;
    }

    /// <summary>Deletes an extension's executable and its index entry.</summary>
    /// <param name="name">The verb.</param>
    /// <returns><c>true</c> if the index named it and it is now gone.</returns>
    public bool Remove(string name) {
        if (Find(name) is not { } record)
            return false;

        var path = PathFor(record.Name);

        if (File.Exists(path))
            File.Delete(path);

        records.RemoveAll(x => string.Equals(x.Name, record.Name, StringComparison.OrdinalIgnoreCase));

        WriteIndex();

        return true;
    }

    /// <summary>Writes the index, creating the directory if it is missing.</summary>
    public void WriteIndex() {
        System.IO.Directory.CreateDirectory(Directory);

        var index = new ExtensionIndex { Format = SupportedFormat, Extensions = [.. records] };

        File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, ExtensionJsonContext.Default.ExtensionIndex));
    }

    /// <summary>The SHA-256 of a file, lower-case hex.</summary>
    /// <param name="path">The file.</param>
    public static string HashOf(string path) {
        using var stream = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    ///     Whether a name may be an extension's verb.
    /// </summary>
    /// <remarks>
    ///     ⚠ Lower-case ASCII, digits and hyphens, starting with a letter. The point is not tidiness:
    ///     the name becomes a file name under <see cref="Directory" />, so <c>.</c>, <c>/</c>,
    ///     <c>\</c> and <c>..</c> have to be impossible rather than sanitized later. It also keeps the
    ///     name comparable with a generated group's, which is always lower-case.
    /// </remarks>
    /// <param name="name">The candidate.</param>
    /// <returns><c>true</c> when the name is safe to use as both a verb and a file name.</returns>
    public static bool IsLegalName(string? name) {
        if (name is not { Length: > 0 and <= 64 })
            return false;

        if (name[0] is not (>= 'a' and <= 'z'))
            return false;

        if (name[^1] == '-')
            return false;

        foreach (var character in name) {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return true;
    }
}

/// <summary>One installed extension, as the index records it.</summary>
sealed class ExtensionRecord {
    /// <summary>The verb it answers to — <c>shell</c> for <c>cyc shell</c>, stored as <c>cyc-shell</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>What the installed file hashed to, lower-case hex. Checked before every launch.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The installed file's length, so an obvious mismatch is caught before the file is read.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Where it was copied from. Recorded for the reader, never used to find the file again.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>When it was installed.</summary>
    [JsonPropertyName("installed")]
    public DateTimeOffset Installed { get; init; }
}

/// <summary>The index file's shape.</summary>
sealed class ExtensionIndex {
    /// <summary>The format version — <see cref="ExtensionStore.SupportedFormat" />.</summary>
    [JsonPropertyName("format")]
    public string Format { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Every installed extension.</summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyList<ExtensionRecord> Extensions { get => field ?? []; init; } = [];
}

/// <summary>
///     The source-generated serializer for the index.
/// </summary>
/// <remarks>
///     ⚠ Source-generated because this project sets <c>IsAotCompatible</c>, the same reason
///     <c>VerbTreeJsonContext</c> exists: a reflective <c>Deserialize&lt;ExtensionIndex&gt;</c> is
///     IL2026 here and fails an ordinary build.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExtensionIndex))]
sealed partial class ExtensionJsonContext : JsonSerializerContext;
