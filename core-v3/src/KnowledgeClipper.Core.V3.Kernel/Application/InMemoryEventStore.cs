using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal interface IEventStore
{
    long Cursor { get; }

    long GetRevision(string streamId);

    IReadOnlyList<KernelEvent> Append(string streamId, long expectedRevision, IReadOnlyList<KernelEvent> events);

    IReadOnlyList<KernelEvent> ReadStream(string streamId);

    IReadOnlyDictionary<string, IReadOnlyList<KernelEvent>> ReadAllStreams();
}

internal sealed class InMemoryEventStore : IEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<KernelEvent>> _streams = new(StringComparer.Ordinal);
    private long _cursor;

    public long Cursor
    {
        get
        {
            lock (_gate)
            {
                return _cursor;
            }
        }
    }

    public long GetRevision(string streamId)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(streamId, out var stream) ? stream.Count : 0;
        }
    }

    public IReadOnlyList<KernelEvent> Append(string streamId, long expectedRevision, IReadOnlyList<KernelEvent> events)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                stream = new List<KernelEvent>();
                _streams.Add(streamId, stream);
            }

            if (stream.Count != expectedRevision)
            {
                throw new EventStoreConcurrencyException(streamId, expectedRevision, stream.Count);
            }

            var appended = new List<KernelEvent>(events.Count);
            foreach (var proposed in events)
            {
                _cursor++;
                var committed = proposed with { Sequence = stream.Count + 1 };
                stream.Add(committed);
                appended.Add(committed);
            }

            return appended;
        }
    }

    public IReadOnlyList<KernelEvent> ReadStream(string streamId)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(streamId, out var stream)
                ? stream.ToArray()
                : Array.Empty<KernelEvent>();
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<KernelEvent>> ReadAllStreams()
    {
        lock (_gate)
        {
            return _streams.ToDictionary(item => item.Key, item => (IReadOnlyList<KernelEvent>)item.Value.ToArray(), StringComparer.Ordinal);
        }
    }
}

internal sealed class EventStoreConcurrencyException : Exception
{
    public EventStoreConcurrencyException(string streamId, long expectedRevision, long actualRevision)
        : base($"Event stream '{streamId}' expected revision {expectedRevision} but is at revision {actualRevision}.")
    {
        StreamId = streamId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string StreamId { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}
