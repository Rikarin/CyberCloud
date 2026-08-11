using System.Threading.Channels;

namespace CyberCloud.Sdk;

/// <summary>
///     Fans one operation's progress array out to every live
///     <see cref="Operation{T}.GetProgressAsync" /> enumerator, in order, exactly once each.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The entries must reach a consumer as they arrive, not in a batch at the end.</b> That
///         is the whole value of docs/plan/21 § The .NET SDK's <c>GetProgressAsync</c> row, and a
///         "streaming" API that buffers is indistinguishable from no API at all for the nine-minute
///         case it exists to serve. Each subscriber gets its own channel, written to from inside the
///         poll, so an entry is readable the instant the poll that carried it returns.
///     </para>
///     <para>
///         ⚠ <b>Unbounded, and that is safe here for a reason worth stating.</b> The producer is the
///         operation's own progress array, which docs/plan/08 § Long-running operations bounds by the
///         reconciler's step count and by the 60-minute timeout; it is not a firehose. A bounded
///         channel would have to choose between blocking the poll — which stalls
///         <c>WaitForCompletionAsync</c> behind a slow consumer — and dropping entries, which makes
///         the stream lie. Both are worse than the memory.
///     </para>
///     <para>
///         A subscriber that arrives late is replayed everything already seen, so enumerating progress
///         after a completed wait yields the whole history rather than nothing.
///     </para>
/// </remarks>
sealed class OperationProgressFeed {
    readonly Lock gate = new();
    readonly List<OperationProgress> seen = [];
    readonly List<Channel<OperationProgress>> subscribers = [];

    bool completed;

    /// <summary>
    ///     Publishes the cumulative array from one poll. Entries already delivered are ignored by
    ///     index — the service resends the whole array every poll (see
    ///     <see cref="OperationStatus.Progress" />), so the new entries are the tail.
    /// </summary>
    public void Publish(IReadOnlyList<OperationProgress> snapshot) {
        lock (gate) {
            for (var i = seen.Count; i < snapshot.Count; i++) {
                seen.Add(snapshot[i]);

                foreach (var subscriber in subscribers)
                    subscriber.Writer.TryWrite(snapshot[i]);
            }
        }
    }

    /// <summary>Ends every subscriber's enumeration. Idempotent.</summary>
    public void Complete() {
        lock (gate) {
            if (completed)
                return;

            completed = true;

            foreach (var subscriber in subscribers)
                subscriber.Writer.TryComplete();
        }
    }

    /// <summary>Opens a subscription, pre-loaded with everything published so far.</summary>
    public Channel<OperationProgress> Subscribe() {
        var channel = Channel.CreateUnbounded<OperationProgress>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (gate) {
            foreach (var entry in seen)
                channel.Writer.TryWrite(entry);

            if (completed)
                channel.Writer.TryComplete();
            else
                subscribers.Add(channel);
        }

        return channel;
    }

    /// <summary>Closes a subscription. Called from the enumerator's <c>finally</c>, including on an early <c>break</c>.</summary>
    public void Unsubscribe(Channel<OperationProgress> channel) {
        lock (gate)
            subscribers.Remove(channel);
    }
}
