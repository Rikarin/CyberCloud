namespace CyberCloud.ResourceManager.Terminals;

/// <summary>
///     The last bytes a shell printed, bounded — what a reconnecting pane is replayed.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/19 § Architecture: <i>"Reconnect replays the ring buffer."</i> The bound is in bytes
///         rather than lines because a terminal stream has no lines — a full-screen program redraws with
///         cursor movements, and one "line" of <c>top</c> can be a kilobyte of escape sequences.
///     </para>
///     <para>
///         ⚠ <b>A replay can start mid-sequence, and that is accepted rather than repaired.</b> Once the
///         ring has wrapped, its first byte is wherever the oldest surviving byte happens to be, which
///         may be inside an escape sequence or a UTF-8 character. The emulator shows one garbled glyph
///         at the top of the replay and recovers on the next sequence; trimming to a "safe" boundary
///         would mean parsing the terminal protocol here, for the benefit of one glyph.
///     </para>
/// </remarks>
/// <param name="capacity">The most bytes kept. The oldest go first.</param>
sealed class OutputRing(int capacity) {
    readonly byte[] buffer = new byte[capacity];

    // The index of the oldest byte, and how many are held.
    int start;
    int count;

    /// <summary>How many bytes the ring holds now.</summary>
    public int Count => count;

    /// <summary>Appends what the shell printed, dropping the oldest bytes past the capacity.</summary>
    /// <param name="chunk">The bytes, in the order they were printed.</param>
    public void Append(ReadOnlySpan<byte> chunk) {
        if (chunk.Length >= buffer.Length) {
            chunk[^buffer.Length..].CopyTo(buffer);
            start = 0;
            count = buffer.Length;
            return;
        }

        foreach (var value in chunk) {
            var end = (start + count) % buffer.Length;
            buffer[end] = value;

            if (count < buffer.Length) {
                count++;
            } else {
                start = (start + 1) % buffer.Length;
            }
        }
    }

    /// <summary>Copies the held bytes out, oldest first.</summary>
    public byte[] Snapshot() {
        var copy = new byte[count];
        var first = Math.Min(count, buffer.Length - start);

        buffer.AsSpan(start, first).CopyTo(copy);
        buffer.AsSpan(0, count - first).CopyTo(copy.AsSpan(first));

        return copy;
    }
}
