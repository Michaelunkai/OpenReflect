using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Format;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>0.2 parallel pipeline: worker count must not change stored bytes (determinism),
/// zstd end-to-end through the engine, and backed-range skipping without I/O.</summary>
public class EngineParallelTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    /// <summary>24 MiB mixed source: alternating 1 MiB random / 1 MiB zeros / 1 MiB random.</summary>
    private static byte[] MakeMixed(int seed = 7)
    {
        var data = new byte[24 << 20];
        var rng = new Random(seed);
        for (int m = 0; m < 24; m++)
        {
            if (m % 3 == 1) continue;                    // zero megabyte
            var seg = data.AsSpan(m << 20, 1 << 20);
            rng.NextBytes(seg);
        }
        return data;
    }

    private static async Task<ImageHeaderInfo> CreateAsync(string src, string dest, ImageOptions o)
    {
        var engine = new ImageEngine();
        var r = await engine.CreateFullAsync(src, dest, o);
        AssertSuccess(r);
        return r.Value;
    }

    private static async Task<List<byte[]>> ReadAllChunksAsync(string image)
    {
        var opened = await OrfImage.OpenAsync(image);
        AssertSuccess(opened);
        await using var r = opened.Value;
        var list = new List<byte[]>();
        for (long i = 0; i < r.Header.LogicalSizeBytes; i += r.Header.ChunkSizeBytes)
        {
            if (!r.HasChunk(i / r.Header.ChunkSizeBytes)) continue;
            var chunk = await r.ReadChunkAsync(i / r.Header.ChunkSizeBytes);
            AssertSuccess(chunk);
            list.Add(chunk.Value);
        }
        return list;
    }

    [Fact]
    public async Task P1_Parallel_Create_Is_Deterministic_Across_Codecs()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            await File.WriteAllBytesAsync(src, MakeMixed());

            foreach (var kind in new[] { CompressionKind.None, CompressionKind.Deflate, CompressionKind.Zstd })
            {
                var a = await CreateAsync(src, Path.Combine(dir, $"a-{kind}.orf"), new ImageOptions { Compression = kind, ChunkSizeBytes = 1 << 20 });
                var b = await CreateAsync(src, Path.Combine(dir, $"b-{kind}.orf"), new ImageOptions { Compression = kind, ChunkSizeBytes = 1 << 20 });
                Assert.Equal(a.LogicalSizeBytes, b.LogicalSizeBytes);
                Assert.Equal(a.ChunkCount, b.ChunkCount);
                var ca = await ReadAllChunksAsync(Path.Combine(dir, $"a-{kind}.orf"));
                var cb = await ReadAllChunksAsync(Path.Combine(dir, $"b-{kind}.orf"));
                Assert.Equal(ca.Count, cb.Count);
                for (int i = 0; i < ca.Count; i++) Assert.Equal(ca[i], cb[i]);
                // zero-skip: chunks 1,4,7,10,13,16,19,22 are all-zero → absent
                Assert.Equal(24 - 8, ca.Count);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task P2_BackedRanges_Skip_Chunks_Without_IO()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            await File.WriteAllBytesAsync(src, MakeMixed());

            // fake provider that declares only the first 8 MiB backed; reads outside are forbidden
            var provider = new RestrictedProvider(src, backedChunks: 8, chunkSize: 1 << 20);
            var engine = new ImageEngine(provider);
            var dest = Path.Combine(dir, "backed.orf");
            var r = await engine.CreateFullAsync(src, dest, new ImageOptions { Compression = CompressionKind.Zstd, ChunkSizeBytes = 1 << 20 });
            AssertSuccess(r);
            Assert.Equal(24L << 20, r.Value.LogicalSizeBytes);

            var opened = await OrfImage.OpenAsync(dest);
            AssertSuccess(opened);
            await using var reader = opened.Value;
            // backed 0..8 MiB: zero MBs at 1,4 (7 is backed-zero too) → stored = 0,2,3,5,6 = 5 chunks
            Assert.Equal(5, reader.Header.ChunkCount > 0 ? CountStored(reader) : -1);
            // logical size stays 24 MiB (restore zero-fills the unbacked tail)
            Assert.Equal(24L << 20, reader.Header.LogicalSizeBytes);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static int CountStored(OrfReader r)
    {
        int n = 0;
        for (long i = 0; i < r.Header.LogicalSizeBytes; i += r.Header.ChunkSizeBytes)
            if (r.HasChunk(i / r.Header.ChunkSizeBytes)) n++;
        return n;
    }

    [Fact]
    public async Task P3_ZeroFillRanges_Skip_File_Extents_Without_IO()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            await File.WriteAllBytesAsync(src, MakeMixed());

            // fake provider declaring MiB 8..12 (chunks 8,9,10,11) zero-fill (pagefile-class);
            // reads inside those extents are forbidden (the "pagefile" is never opened)
            var provider = new ZeroFillProvider(src, new BackingRange(8L << 20, 4L << 20));
            var engine = new ImageEngine(provider);
            var dest = Path.Combine(dir, "zerofill.orf");
            var r = await engine.CreateFullAsync(src, dest, new ImageOptions { Compression = CompressionKind.Zstd, ChunkSizeBytes = 1 << 20 });
            AssertSuccess(r);
            Assert.Equal(24L << 20, r.Value.LogicalSizeBytes);

            var opened = await OrfImage.OpenAsync(dest);
            AssertSuccess(opened);
            await using var reader = opened.Value;
            // P1 baseline stores 16 chunks (MBs 1,4,7,10,13,16,19,22 are zero-skip). The
            // exclusion covers chunks 8,9,10,11; chunk 10 was already zero, so 3 more vanish → 13.
            Assert.Equal(13, CountStored(reader));
            foreach (var idx in new long[] { 8, 9, 10, 11 })
                Assert.False(reader.HasChunk(idx), $"chunk {idx} must be excluded (restored as zeros)");
            // content outside the exclusion is untouched
            var chunk12 = await reader.ReadChunkAsync(12);
            AssertSuccess(chunk12);
            Assert.Equal(1 << 20, chunk12.Value.Length);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task P4_Parallel_Readers_Match_Single_Reader_Output()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            await File.WriteAllBytesAsync(src, MakeMixed());

            // session hands out independent FileStreams -> engine fans out interleaved readers
            var multi = new MultiStreamProvider(src);
            var engine = new ImageEngine(multi);
            var dest = Path.Combine(dir, "multi.orf");
            var r = await engine.CreateFullAsync(src, dest, new ImageOptions { Compression = CompressionKind.Zstd, ChunkSizeBytes = 1 << 20 });
            AssertSuccess(r);

            var single = await CreateAsync(src, Path.Combine(dir, "single.orf"), new ImageOptions { Compression = CompressionKind.Zstd, ChunkSizeBytes = 1 << 20 });
            Assert.Equal(single.ChunkCount, r.Value.ChunkCount);
            var a = await ReadAllChunksAsync(dest);
            var b = await ReadAllChunksAsync(Path.Combine(dir, "single.orf"));
            Assert.Equal(b.Count, a.Count);
            for (int i = 0; i < a.Count; i++) Assert.Equal(b[i], a[i]);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task P5_Skip_Entries_Never_Deadlock_The_Watermark()
    {
        // Regression: unbacked skip entries used to hold watermark permits while sitting in the
        // results map AHEAD of the ordered writer. On a real 2 TB disk the readers race through
        // long unallocated runs, exhaust all (workers*4) permits, and the reader of the next
        // BACKED chunk is starved forever — the writer waits for a chunk nobody will ever read.
        // Structure here: 1900 MiB source, ONLY chunk 200 backed; reads at chunk offsets
        // (offset % 8 MiB == 0) are slowed so the backed chunk's reader lags while the other
        // interleaved readers blast through the unbacked tail.
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            var data = new byte[1900 << 20];
            var rng = new Random(11);
            rng.NextBytes(data.AsSpan(200 << 20, 1 << 20));   // only the backed chunk matters
            await File.WriteAllBytesAsync(src, data);

            var provider = new StarveProvider(src, backedChunk: 200, chunkSize: 1 << 20);
            var engine = new ImageEngine(provider);
            var dest = Path.Combine(dir, "starve.orf");
            var create = engine.CreateFullAsync(src, dest, new ImageOptions { Compression = CompressionKind.Zstd, ChunkSizeBytes = 1 << 20 });
            var finished = await Task.WhenAny(create, Task.Delay(TimeSpan.FromMinutes(3)));
            Assert.True(finished == create, "pipeline deadlocked: skip entries starved the reader of the backed chunk");
            var r = create.Result;
            AssertSuccess(r);
            Assert.Equal(1900L << 20, r.Value.LogicalSizeBytes);

            var opened = await OrfImage.OpenAsync(dest);
            AssertSuccess(opened);
            await using var reader = opened.Value;
            Assert.True(reader.HasChunk(200), "backed chunk must be stored");
            Assert.False(reader.HasChunk(199), "unbacked chunk must be absent");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>Independent FileStreams + only one backed chunk + slowed stride-0 reads:
    /// reproduces the skip-starvation deadlock geometry deterministically.</summary>
    private sealed class StarveProvider : ISnapshotProvider
    {
        private readonly string _path;
        private readonly int _backedChunk, _chunkSize;
        public StarveProvider(string path, int backedChunk, int chunkSize) { _path = path; _backedChunk = backedChunk; _chunkSize = chunkSize; }
        public Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
            => Task.FromResult(Result<ISnapshotSession>.Ok(new StarveSession(_path, _backedChunk * (long)_chunkSize, _chunkSize)));
    }

    private sealed class StarveSession : ISnapshotSession
    {
        private readonly string _path;
        private readonly long _backedOffset, _chunkSize;
        public StarveSession(string path, long backedOffset, long chunkSize) { _path = path; _backedOffset = backedOffset; _chunkSize = chunkSize; }
        public string Description => "starve-fake";
        public bool SupportsIndependentStreams => true;
        public System.Collections.Generic.IReadOnlyList<BackingRange> BackedRanges => new[] { new BackingRange(_backedOffset, _chunkSize) };
        public Stream OpenReadStream() => NewStream();
        public Stream? TryOpenIndependentStream() => NewStream();
        private Stream NewStream() => new SlowStrideStream(_path, _chunkSize);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>FileStream that sleeps on reads whose offset is chunk-aligned to stride 0,
    /// delaying exactly one interleaved reader.</summary>
    private sealed class SlowStrideStream : Stream
    {
        private readonly FileStream _fs;
        private readonly long _chunkSize;
        public SlowStrideStream(string path, long chunkSize) { _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true); _chunkSize = chunkSize; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _fs.Length;
        public override long Position { get => _fs.Position; set => _fs.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _fs.Read(buffer, offset, count);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_fs.Position % (8 * _chunkSize) == 0) await Task.Delay(30, ct).ConfigureAwait(false);
            return await _fs.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => _fs.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _fs.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Pass-through that serves an independent FileStream per OpenReadStream call.</summary>
    private sealed class MultiStreamProvider : ISnapshotProvider
    {
        private readonly string _path;
        public MultiStreamProvider(string path) { _path = path; }
        public Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
            => Task.FromResult(Result<ISnapshotSession>.Ok(new MultiStreamSession(_path)));
    }

    private sealed class MultiStreamSession : ISnapshotSession
    {
        private readonly string _path;
        public MultiStreamSession(string path) { _path = path; }
        public string Description => "multi-stream-fake";
        public bool SupportsIndependentStreams => true;
        public Stream OpenReadStream() => NewStream();
        public Stream? TryOpenIndependentStream() => NewStream();
        private FileStream NewStream() => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Pass-through that forbids reads inside the declared zero-fill extents.</summary>
    private sealed class ZeroFillProvider : ISnapshotProvider
    {
        private readonly string _path;
        private readonly BackingRange _zeroFill;
        public ZeroFillProvider(string path, BackingRange zeroFill) { _path = path; _zeroFill = zeroFill; }

        public Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
        {
            var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            return Task.FromResult(Result<ISnapshotSession>.Ok(new Session(fs, _zeroFill)));
        }

        private sealed class Session : ISnapshotSession
        {
            private readonly FileStream _fs;
            public Session(FileStream fs, BackingRange zeroFill) { _fs = fs; ZeroFillRanges = new[] { zeroFill }; }
            public string Description => "zerofill-fake";
            public System.Collections.Generic.IReadOnlyList<BackingRange> ZeroFillRanges { get; }
            public Stream OpenReadStream() => new ForbidRangeStream(_fs, ZeroFillRanges[0].Offset, ZeroFillRanges[0].Length);
            public async ValueTask DisposeAsync() => await _fs.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Throws if anything reads inside the forbidden span (proves exclusion = no I/O).</summary>
    private sealed class ForbidRangeStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _from, _to;
        public ForbidRangeStream(Stream inner, long from, long length) { _inner = inner; _from = from; _to = from + length; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        private void Check(long pos, long count) { if (pos + count > _from && pos < _to) throw new IOException("read inside zero-fill range"); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Check(_inner.Position, count);
            return _inner.Read(buffer, offset, count);
        }
        public override async System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Check(_inner.Position, count);
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _inner.Position + offset, _ => _inner.Length + offset };
            Check(target, 1);
            return _inner.Seek(target, SeekOrigin.Begin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Pass-through that forbids reads outside the declared backed range.</summary>
    private sealed class RestrictedProvider : ISnapshotProvider
    {
        private readonly string _path;
        private readonly int _backedChunks;
        private readonly int _chunkSize;
        public RestrictedProvider(string path, int backedChunks, int chunkSize) { _path = path; _backedChunks = backedChunks; _chunkSize = chunkSize; }

        public Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
        {
            var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            ISnapshotSession s = new RestrictedSession(fs, (long)_backedChunks * _chunkSize);
            return Task.FromResult(Result<ISnapshotSession>.Ok(s));
        }
    }

    private sealed class RestrictedSession : ISnapshotSession
    {
        private readonly FileStream _fs;
        public RestrictedSession(FileStream fs, long backedBytes) { _fs = fs; BackedRanges = new[] { new BackingRange(0, backedBytes) }; }
        public string Description => "restricted";
        public System.Collections.Generic.IReadOnlyList<BackingRange>? BackedRanges { get; }
        public Stream OpenReadStream()
            => new GuardStream(_fs, BackedRanges![0].Offset + BackedRanges![0].Length);
        public async ValueTask DisposeAsync() => await _fs.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Throws if anything reads beyond the backed length (proves skip = no I/O).</summary>
    private sealed class GuardStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        public GuardStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_inner.Position + count > _limit) throw new IOException("read outside backed range");
            return _inner.Read(buffer, offset, count);
        }
        public override async System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_inner.Position + count > _limit) throw new IOException("read outside backed range");
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _inner.Position + offset, _ => _inner.Length + offset };
            if (target > _limit && target != _inner.Length) throw new IOException("seek outside backed range");
            return _inner.Seek(Math.Min(target, _limit), SeekOrigin.Begin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
