using System.Text;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Redis Cluster's key → slot function, so that "all of a tenant's keys land on one shard"
///     (docs/plan/05 § Hot) is something the platform can assert rather than assume.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this is written out rather than taken from StackExchange.Redis.</b>
///         <c>IConnectionMultiplexer.HashSlot(RedisKey)</c> exists and is public, and it returns
///         <c>-1</c> — <c>ServerSelectionStrategy.NoSlot</c> — whenever the multiplexer's
///         <c>ServerType</c> is <c>Standalone</c>. Verified by decompiling StackExchange.Redis
///         3.1.13: <c>HashSlot(in RedisKey)</c> opens with
///         <c>if (ServerType != ServerType.Standalone)</c> and falls through to <c>-1</c>. So it
///         cannot answer "which slot would this key take" against a single-node Redis, which is what
///         a developer machine and most of CI have; it only works once the client has negotiated
///         cluster mode. The tests cross-check this implementation against a real cluster-mode
///         server, and use this one everywhere else.
///     </para>
///     <para>
///         The algorithm is the one in the Redis Cluster specification: CRC16-CCITT (polynomial
///         <c>0x1021</c>, initial value <c>0</c>, no input or output reflection — the variant usually
///         labelled XMODEM) over the key bytes, modulo 16 384, with the hash tag rule applied first.
///     </para>
/// </remarks>
public static class RedisHashSlot
{
    /// <summary>The number of slots in a Redis Cluster keyspace.</summary>
    public const int SlotCount = 16384;

    /// <summary>The slot a key would be routed to.</summary>
    /// <param name="key">The full key, braces and all.</param>
    public static int Of(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Crc16(Encoding.UTF8.GetBytes(HashTagOf(key))) % SlotCount;
    }

    /// <summary>
    ///     The part of a key that actually determines its slot: the text between the first
    ///     <c>{</c> and the first <c>}</c> after it, if that text is non-empty; otherwise the whole
    ///     key.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two degenerate cases are the ones worth being explicit about, because both are
    ///         real key layouts somebody could write and both "work" on a single node:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b><c>{}</c> — empty tag.</b> Redis falls back to hashing the whole key, so a
    ///             layout with empty braces has no colocation at all.
    ///         </item>
    ///         <item>
    ///             <b>Braces around the whole key.</b> Legal, and every key gets its own slot, which
    ///             is the same as having no tag. This is the mistake docs/plan/05 § Hot is guarding
    ///             against and it costs a tenant delete going from one <c>SCAN</c> on one shard to a
    ///             fan-out across twelve.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static string HashTagOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var open = key.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            return key;
        }

        var close = key.IndexOf('}', open + 1);
        return close > open + 1 ? key[(open + 1)..close] : key;
    }

    /// <summary>CRC16-CCITT/XMODEM, the function Redis Cluster uses to pick a slot.</summary>
    static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0;

        foreach (var b in data)
        {
            crc ^= b << 8;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? ((crc << 1) ^ 0x1021) & 0xFFFF
                    : (crc << 1) & 0xFFFF;
            }
        }

        return crc;
    }
}
