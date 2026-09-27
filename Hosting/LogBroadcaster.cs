using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SampleLogGenerator.Models;

namespace SampleLogGenerator.Hosting;

/// <summary>Fans live log lines out to streaming (SSE) subscribers. Slow subscribers drop their oldest lines.</summary>
public sealed class LogBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<LogEntry>> _subscribers = new();

    public void Publish(IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0) return;
        foreach (var channel in _subscribers.Values)
            foreach (var entry in entries)
                channel.Writer.TryWrite(entry);
    }

    public async IAsyncEnumerable<LogEntry> Subscribe(Func<LogEntry, bool> filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct))
                if (filter(entry))
                    yield return entry;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
