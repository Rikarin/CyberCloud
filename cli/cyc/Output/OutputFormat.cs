namespace CyberCloud.Cli.Output;

/// <summary>
///     docs/plan/21 § Decisions: <i>"<c>table</c> for humans, <c>json</c> for scripts. <c>tsv</c>
///     because <c>cut</c> exists and people use it."</i>
/// </summary>
enum OutputFormat {
    /// <summary>Aligned columns, the default. For reading, never for parsing.</summary>
    Table,

    /// <summary>Indented JSON. ⚠ The only format whose failure behaviour is a contract — see <see cref="ResultWriter" />.</summary>
    Json,

    /// <summary>YAML, for a human reading a nested document a table cannot hold.</summary>
    Yaml,

    /// <summary>Tab-separated, no header — what <c>cut -f2</c> expects.</summary>
    Tsv,

    /// <summary>Nothing at all. The exit code is the answer.</summary>
    None,
}

/// <summary>Parsing and naming for <see cref="OutputFormat" />.</summary>
static class OutputFormats {
    /// <summary>Every format's name, in the order the generated tree's <c>choices</c> lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["table", "json", "yaml", "tsv", "none"];

    /// <summary>Parses a <c>--output</c> value.</summary>
    /// <param name="value">The name, or <c>null</c> for <see cref="OutputFormat.Table" />.</param>
    /// <exception cref="CycUsageException">The name is not one of <see cref="Names" />.</exception>
    public static OutputFormat Parse(string? value)
        => value switch {
            null or "" or "table" => OutputFormat.Table,
            "json" => OutputFormat.Json,
            "yaml" => OutputFormat.Yaml,
            "tsv" => OutputFormat.Tsv,
            "none" => OutputFormat.None,
            _ => throw new CycUsageException(
                $"'{value}' is not an output format. Available: {string.Join(", ", Names)}."),
        };

    /// <summary>
    ///     The name <c>--output</c> spells a format with — the inverse of <see cref="Parse" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ A switch rather than an index into <see cref="Names" />. The two lists happen to be in
    ///     the same order and nothing enforces that; an index would turn reordering the enum into a
    ///     CLI that tells an extension the wrong format. <c>OutputFormatTests</c> round-trips every
    ///     value through <see cref="Parse" />.
    /// </remarks>
    /// <param name="format">The format.</param>
    public static string NameOf(OutputFormat format)
        => format switch {
            OutputFormat.Json => "json",
            OutputFormat.Yaml => "yaml",
            OutputFormat.Tsv => "tsv",
            OutputFormat.None => "none",
            _ => "table",
        };
}
