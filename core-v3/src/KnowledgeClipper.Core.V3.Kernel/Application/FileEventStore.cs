using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed partial class FileEventStore : IEventStore
{
    private readonly string _streamsRoot;
    private readonly ConcurrentDictionary<string, object> _streamLocks = new(StringComparer.Ordinal);
    private long _cursor;

    public FileEventStore(string root)
    {
        Root = Path.GetFullPath(root);
        _streamsRoot = Path.Combine(Root, "streams");
        Directory.CreateDirectory(_streamsRoot);
        _cursor = RecoverCursor();
    }

    public string Root { get; }

    public long Cursor => Interlocked.Read(ref _cursor);

    public long GetRevision(string streamId)
    {
        ValidateStreamId(streamId);
        lock (_streamLocks.GetOrAdd(streamId, static _ => new object()))
        {
            return ReadStreamCore(streamId).Count;
        }
    }

    public IReadOnlyList<KernelEvent> Append(string streamId, long expectedRevision, IReadOnlyList<KernelEvent> events)
    {
        ValidateStreamId(streamId);
        if (events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(events));
        }

        lock (_streamLocks.GetOrAdd(streamId, static _ => new object()))
        {
            var actualRevision = ReadStreamCore(streamId).Count;
            if (actualRevision != expectedRevision)
            {
                throw new EventStoreConcurrencyException(streamId, expectedRevision, actualRevision);
            }

            var committed = events
                .Select((item, index) => item with { StreamId = streamId, Sequence = actualRevision + index + 1 })
                .ToArray();
            var first = committed[0].Sequence;
            var last = committed[^1].Sequence;
            var streamDirectory = GetStreamDirectory(streamId);
            Directory.CreateDirectory(streamDirectory);
            var finalName = $"{first:D20}-{last:D20}.events.ndjson";
            var finalPath = Path.Combine(streamDirectory, finalName);
            var pendingPath = Path.Combine(streamDirectory, $".pending-{Guid.NewGuid():N}.ndjson");
            try
            {
                using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    foreach (var item in committed)
                    {
                        var line = JsonSerializer.Serialize(item, KernelJson.Options) + "\n";
                        var bytes = Encoding.UTF8.GetBytes(line);
                        stream.Write(bytes);
                    }

                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(pendingPath, finalPath, overwrite: false);
                }
                catch (IOException)
                {
                    var nowRevision = ReadStreamCore(streamId).Count;
                    throw new EventStoreConcurrencyException(streamId, expectedRevision, nowRevision);
                }

                Interlocked.Add(ref _cursor, committed.Length);
                return committed;
            }
            finally
            {
                if (File.Exists(pendingPath))
                {
                    File.Delete(pendingPath);
                }
            }
        }
    }

    public IReadOnlyList<KernelEvent> ReadStream(string streamId)
    {
        ValidateStreamId(streamId);
        lock (_streamLocks.GetOrAdd(streamId, static _ => new object()))
        {
            return ReadStreamCore(streamId);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<KernelEvent>> ReadAllStreams()
    {
        return Directory.EnumerateDirectories(_streamsRoot)
            .Select(Path.GetFileName)
            .Where(streamId => streamId is not null)
            .OrderBy(streamId => streamId, StringComparer.Ordinal)
            .ToDictionary(streamId => streamId!, streamId => ReadStream(streamId!), StringComparer.Ordinal);
    }

    private IReadOnlyList<KernelEvent> ReadStreamCore(string streamId)
    {
        var directory = GetStreamDirectory(streamId);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<KernelEvent>();
        }

        var segments = Directory.EnumerateFiles(directory, "*.events.ndjson", SearchOption.TopDirectoryOnly)
            .Select(path => ParseSegmentName(path))
            .OrderBy(item => item.First)
            .ThenBy(item => item.Last)
            .ToArray();
        var events = new List<KernelEvent>();
        long expectedSequence = 1;
        foreach (var segment in segments)
        {
            if (segment.First != expectedSequence || segment.Last < segment.First)
            {
                throw new InvalidDataException($"Late, overlapping, or noncontiguous event segment '{Path.GetFileName(segment.Path)}' in stream '{streamId}'.");
            }

            var lines = File.ReadAllLines(segment.Path, new UTF8Encoding(false, true));
            if (lines.Length != segment.Last - segment.First + 1 || lines.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Truncated event segment '{Path.GetFileName(segment.Path)}' in stream '{streamId}'.");
            }

            foreach (var line in lines)
            {
                using var json = StrictJson.Parse(Encoding.UTF8.GetBytes(line), segment.Path);
                var item = json.RootElement.Deserialize<KernelEvent>(KernelJson.Options)
                    ?? throw new InvalidDataException($"Event in '{segment.Path}' deserialized to null.");
                if (item.Schema != KernelSchemas.Event || item.StreamId != streamId || item.Sequence != expectedSequence)
                {
                    throw new InvalidDataException($"Event identity or sequence mismatch in '{segment.Path}'.");
                }

                events.Add(item);
                expectedSequence++;
            }

            if (expectedSequence - 1 != segment.Last)
            {
                throw new InvalidDataException($"Event segment range mismatch in '{segment.Path}'.");
            }
        }

        return events;
    }

    private long RecoverCursor()
    {
        long count = 0;
        foreach (var directory in Directory.EnumerateDirectories(_streamsRoot))
        {
            var streamId = Path.GetFileName(directory);
            ValidateStreamId(streamId);
            count += ReadStreamCore(streamId).Count;
        }

        return count;
    }

    private string GetStreamDirectory(string streamId)
    {
        return Path.Combine(_streamsRoot, streamId);
    }

    private static Segment ParseSegmentName(string path)
    {
        var match = SegmentNamePattern().Match(Path.GetFileName(path));
        if (!match.Success ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var last))
        {
            throw new InvalidDataException($"Invalid event segment name '{Path.GetFileName(path)}'.");
        }

        return new Segment(path, first, last);
    }

    private static void ValidateStreamId(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId) || streamId.Length > 128 || !streamId.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new InvalidDataException($"Invalid event stream id '{streamId}'.");
        }
    }

    private sealed record Segment(string Path, long First, long Last);

    [GeneratedRegex("^([0-9]{20})-([0-9]{20})\\.events\\.ndjson$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentNamePattern();
}
