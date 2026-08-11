namespace CyberCloud.Cli.Output;

/// <summary>
///     The two formats that flatten a document into rows: <c>table</c> for a human and <c>tsv</c> for
///     <c>cut</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Both drop what they cannot flatten, and that is why neither is safe to parse.</b> A
///     nested object becomes one cell of compact JSON, and a document whose shape changes between
///     releases changes its columns with it. docs/plan/21 § Decisions is explicit that <c>json</c> is
///     the format for scripts; <c>tsv</c> exists because people pipe it to <c>cut</c> anyway, and the
///     honest thing is to make its columns stable rather than to pretend it is a contract.
/// </remarks>
static class TableWriter {
    /// <summary>Writes aligned columns with a header.</summary>
    /// <param name="writer">The stream to write to.</param>
    /// <param name="value">The value.</param>
    public static void WriteTable(TextWriter writer, Payload value) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var table = Flatten(value);

        if (table.Rows.Count == 0)
            return;

        var widths = new int[table.Columns.Count];

        for (var column = 0; column < table.Columns.Count; column++) {
            widths[column] = table.Columns[column].Length;

            foreach (var row in table.Rows)
                widths[column] = Math.Max(widths[column], row[column].Length);
        }

        if (table.HasHeader) {
            writer.WriteLine(Line(table.Columns, widths));
            writer.WriteLine(Line([.. widths.Select(x => new string('-', x))], widths));
        }

        foreach (var row in table.Rows)
            writer.WriteLine(Line(row, widths));
    }

    /// <summary>Writes tab-separated rows, with no header.</summary>
    /// <param name="writer">The stream to write to.</param>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     ⚠ No header, deliberately. <c>cyc … --output tsv | cut -f2</c> is the use, and a header
    ///     line is one row of garbage every such pipeline has to skip.
    /// </remarks>
    public static void WriteTsv(TextWriter writer, Payload value) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        foreach (var row in Flatten(value).Rows)
            writer.WriteLine(string.Join('\t', row.Select(Escape)));
    }

    /// <summary>
    ///     Turns a value into rows and columns.
    /// </summary>
    /// <remarks>
    ///     Three shapes, because three shapes is what the platform returns: an array of objects is a
    ///     table, one object is a two-column property list, and a scalar is one cell.
    /// </remarks>
    static (IReadOnlyList<string> Columns, List<string[]> Rows, bool HasHeader) Flatten(Payload value) {
        if (value.IsMissing)
            return ([], [], false);

        if (value.IsArray) {
            var elements = value.Items.ToList();

            if (elements.Count == 0)
                return ([], [], false);

            if (elements.TrueForAll(x => x.IsObject)) {
                // Column order is first appearance, not alphabetical: the platform puts `name` and
                // `location` before `properties` and a human reads the columns in that order.
                var columns = new List<string>();

                foreach (var element in elements) {
                    foreach (var member in element.Members) {
                        if (!columns.Contains(member.Key, StringComparer.Ordinal))
                            columns.Add(member.Key);
                    }
                }

                var rows = elements
                    .Select(element => columns.Select(column => element.Member(column).ToCell()).ToArray())
                    .ToList();

                return (columns, rows, true);
            }

            return (["Value"], [.. elements.Select(x => new[] { x.ToCell() })], false);
        }

        if (value.IsObject) {
            var rows = value.Members.Select(x => new[] { x.Key, x.Value.ToCell() }).ToList();

            return (["Name", "Value"], rows, true);
        }

        return (["Value"], [[value.ToCell()]], false);
    }

    static string Line(IReadOnlyList<string> cells, int[] widths) {
        var line = new StringBuilder();

        for (var i = 0; i < cells.Count; i++) {
            if (i > 0)
                line.Append("  ");

            line.Append(i == cells.Count - 1 ? cells[i] : cells[i].PadRight(widths[i]));
        }

        return line.ToString().TrimEnd();
    }

    /// <summary>
    ///     Makes a cell safe for a tab-separated row.
    /// </summary>
    /// <remarks>
    ///     ⚠ A tab or a newline inside a value would add a column or a row to somebody's
    ///     <c>cut</c> pipeline. Escaping them as <c>\t</c> and <c>\n</c> is what <c>az</c> does and it
    ///     keeps the row count equal to the record count, which is the property the pipeline relies
    ///     on.
    /// </remarks>
    static string Escape(string cell)
        => cell
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
