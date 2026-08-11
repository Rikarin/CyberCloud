using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Storage;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Grain state as JSON, which is the whole justification docs/plan/05 § Serialization and schema
///     evolution gives for the durable tier's format: "in year two someone will need to answer 'what
///     did this resource look like before the bad deploy' with <c>psql</c>, and a binary blob makes
///     that a program rather than a query".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/05 § Storage provider wiring names a type that does not exist.</b> Its
///         body sets <c>o.GrainStorageSerializer = new OrleansJsonGrainStorageSerializer()</c>.
///         There is no such type in Orleans 10.2.2. The two that do exist are
///         <c>Orleans.Storage.JsonGrainStorageSerializer</c>, which is <b>Newtonsoft</b>-based (it
///         takes an <c>OrleansJsonSerializer</c>), and <c>Orleans.Storage.OrleansGrainStorageSerializer</c>,
///         which is the binary one. The § Serialization section separately says JSON "via
///         <c>Microsoft.Orleans.Serialization.SystemTextJson</c>" — and that package ships a
///         <c>JsonCodec</c> for the <i>Orleans</i> serializer, not an <see cref="IGrainStorageSerializer" />.
///         So the doc's stated combination is not obtainable from a package, and this is it, written
///         out: twenty lines over <c>System.Text.Json</c>, which is in the shared framework and adds
///         no dependency and no Newtonsoft.
///     </para>
///     <para>
///         ⚠ <b>Choosing JSON changes what the evolution rules in docs/plan/05 § Serialization
///         actually protect.</b> Rules 2 and 5 there — "<c>[Id(n)]</c> numbers are never reused,
///         never reordered", "renaming a type without <c>[Alias]</c> is a data-loss bug" — describe
///         the <i>Orleans binary</i> wire format, where the number is the identity of a member and
///         the alias is the identity of a type. Under JSON neither attribute is read at all: the
///         <b>property name</b> is the persisted contract and renaming a C# property is the data-loss
///         bug. Both sets of rules are worth keeping (the same types cross the wire between grains,
///         where <c>[Id]</c> and <c>[Alias]</c> do govern), but the durable tier's compatibility test
///         has to check names, and docs/plan/05 does not say so.
///     </para>
/// </remarks>
public sealed class SystemTextJsonGrainStorageSerializer : IGrainStorageSerializer
{
    /// <summary>The options every durable payload is written with.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>WriteIndented</c> is off: the readability that matters is <c>psql</c>'s, and
    ///         <c>jsonb_pretty()</c> or <c>jq</c> supply it on demand rather than in every row of
    ///         every backup. Nulls are kept rather than skipped, because "the field was absent" and
    ///         "the field was null" are different answers to the year-two question and dropping the
    ///         distinction to save bytes trades the exact property the format was chosen for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The encoder is the setting that decides whether the format delivers what it was
    ///         chosen for.</b> <c>System.Text.Json</c>'s default escapes far more than JSON requires
    ///         — every non-ASCII character, and <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>,
    ///         <c>+</c> — so a resource called <c>Müller's Datenbank</c> is stored as
    ///         <c>"Müller's Datenbank"</c>. That is still valid JSON and it is not
    ///         "answerable with <c>psql</c>", which is the entire justification docs/plan/05
    ///         § Serialization gives for paying JSON's 2-3x size over MemoryPack.
    ///         <c>UnsafeRelaxedJsonEscaping</c> is the documented way to stop it. The word "unsafe"
    ///         refers to emitting JSON straight into an HTML document without further encoding; grain
    ///         state goes into a <c>bytea</c> column and comes back through a serializer, so that
    ///         boundary is not here. Anything that renders this state to a browser re-encodes it at
    ///         its own boundary, which is where the escaping belongs.
    ///     </para>
    /// </remarks>
    public static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly JsonSerializerOptions options;

    /// <summary>Creates a serializer with <see cref="DefaultOptions" />.</summary>
    public SystemTextJsonGrainStorageSerializer()
        : this(DefaultOptions)
    {
    }

    /// <summary>Creates a serializer with explicit options.</summary>
    /// <param name="options">The <see cref="System.Text.Json" /> options to use.</param>
    public SystemTextJsonGrainStorageSerializer(JsonSerializerOptions options)
    {
        this.options = options;
    }

    /// <inheritdoc />
    public BinaryData Serialize<T>(T input) =>
        new(JsonSerializer.SerializeToUtf8Bytes(input, options));

    /// <inheritdoc />
    public T Deserialize<T>(BinaryData input) =>
        JsonSerializer.Deserialize<T>(input.ToMemory().Span, options)
        ?? throw new InvalidOperationException(
            $"Grain state deserialised to null for {typeof(T)}. The stored payload was "
            + $"{input.ToMemory().Length} bytes.");
}
