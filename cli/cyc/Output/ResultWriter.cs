namespace CyberCloud.Cli.Output;

/// <summary>
///     Renders one command's answer to stdout, in the requested format.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing this type writes is ever a sentence.</b> The whole failure contract of
///         <c>--output json</c> lives here and in <see cref="CycConsole" />: stdout carries the
///         payload or carries nothing, so a script running <c>cyc … --output json | jq .</c> never has
///         to defend itself against a human-readable error arriving mid-document. Failures are
///         rendered by <see cref="ErrorWriter" />, to stderr, and this type is not involved.
///     </para>
///     <para>
///         ⚠ <b>A failed command writes no partial document.</b> A renderer that had already emitted
///         half an array when the second page failed would produce invalid JSON on stdout, which is
///         the same defect wearing a different hat. Every format here renders a value that is already
///         complete — the one streaming thing <c>cyc</c> does, long-running-operation progress, goes
///         to stderr.
///     </para>
/// </remarks>
static class ResultWriter {
    /// <summary>Writes a value.</summary>
    /// <param name="console">The console. Only <see cref="CycConsole.Out" /> is touched.</param>
    /// <param name="format">The format.</param>
    /// <param name="value">The value, after <c>--query</c> has been applied.</param>
    public static void Write(CycConsole console, OutputFormat format, Payload value) {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(value);

        switch (format) {
            case OutputFormat.None:
                break;

            case OutputFormat.Json:
                // ⚠ A missing value is `null`, not nothing: `--output json` promises a document.
                console.Out.WriteLine(value.ToJson(indented: true));

                break;

            case OutputFormat.Yaml:
                YamlWriter.Write(console.Out, value);

                break;

            case OutputFormat.Tsv:
                TableWriter.WriteTsv(console.Out, value);

                break;

            default:
                TableWriter.WriteTable(console.Out, value);

                break;
        }

        console.Out.Flush();
    }
}
