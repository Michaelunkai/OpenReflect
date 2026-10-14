using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>A read-only stream over the union of extent ranges. Each extent opens its backing
/// stream lazily (device, VSS shadow device); gaps between extents must never be read (the engine
/// only reads chunks inside declared BackedRanges) and throw defensively if attempted.</summary>
public sealed class MultiExtentStream : Stream
{
    public sealed record Extent(long Offset, long Length, string Description, Func<Stream> Opener);

    private readonly Extent[] _extents;
    private readonly long _length;
    private readonly Dictionary<int, Stream> _open = new();
    private long _position;

    public MultiExtentStream(IEnumerable<Extent> extents, long length)
    {
        _extents = extents.OrderBy(e => e.Offset).ToArray();
        for (int i = 1; i < _extents.Length; i++)
            if (_extents[i - 1].Offset + _extents[i - 1].Length > _extents[i].Offset)
                throw new ArgumentException("extents overlap");
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    private (int Index, long InnerOffset) Locate(long absolute)
    {
        // binary search: last extent with Offset <= absolute
        int lo = 0, hi = _extents.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_extents[mid].Offset <= absolute) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return (-1, 0);
        long inner = absolute - _extents[found].Offset;
        if (inner >= _extents[found].Length) return (-1, 0);
        return (found, inner);
    }

    private Stream StreamFor(int index)
    {
        if (!_open.TryGetValue(index, out var s))
        {
            s = _extents[index].Opener();
            _open[index] = s;
        }
        return s;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var s in _open.Values) await s.DisposeAsync().ConfigureAwait(false);
        _open.Clear();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new InvalidOperationException("use ReadAsync (engine pipeline is async)");

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        long remaining = _length - _position;
        if (remaining <= 0) return 0;
        int want = (int)Math.Min(count, remaining);
        int total = 0;
        while (total < want)
        {
            var (idx, inner) = Locate(_position + total);
            if (idx < 0) throw new IOException($"read of unbacked region at offset {_position + total} (intelligent-sector gap)");
            var extent = _extents[idx];
            long inExtent = Math.Min(want - total, extent.Length - inner);
            var s = StreamFor(idx);
            s.Seek(inner, SeekOrigin.Begin);
            int done = 0;
            while (done < inExtent)
            {
                int n = await s.ReadAsync(buffer.AsMemory(offset + total + done, (int)(inExtent - done)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (Environment.GetEnvironmentVariable("ORF_DEBUG") == "1")
                        Console.Error.WriteLine($"##EOFDBG desc={extent.Description} extOff={extent.Offset} extLen={extent.Length} inner={inner} done={done} inExtent={inExtent} pos={_position} total={total} want={want}");
                    throw new EndOfStreamException($"{extent.Description} ended at +{inner + done} of {inExtent} requested");
                }
                done += n;
            }
            total += (int)inExtent;
        }
        _position += total;
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (_position < 0) throw new IOException("seek before start");
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
