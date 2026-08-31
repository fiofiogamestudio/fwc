using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Fw.Rt.Archives;

using Fw.Rt.Bridge;

public sealed record FrameArchiveOptions(
    int CompressionThresholdBytes = 900,
    int MaxContentBytes = 8 * 1024 * 1024,
    int MaxFrameBytes = 8 * 1024 * 1024,
    int MaxEncodedBytes = 8 * 1024 * 1024,
    int FlushIntervalFrames = 30
);

public sealed record FrameArchiveFrame(long Tick, bool IsCheckpoint, byte[] Payload);

public sealed record FrameArchiveRecovery(
    long ValidLength,
    int FrameCount,
    long LastTick,
    bool Truncated
);

public static class FrameArchive
{
    public static FrameArchiveWriter Create(
        string path,
        ReadOnlySpan<byte> content,
        FrameArchiveOptions? options = null
    ) => FrameArchiveWriter.Create(path, content, options ?? new FrameArchiveOptions());

    public static FrameArchiveReader Open(string path, FrameArchiveOptions? options = null) =>
        FrameArchiveReader.Open(path, options ?? new FrameArchiveOptions());

    public static FrameArchiveRecovery Recover(string path, FrameArchiveOptions? options = null) =>
        FrameArchiveReader.Recover(path, options ?? new FrameArchiveOptions());
}

public sealed class FrameArchiveWriter : IDisposable
{
    private readonly FrameArchiveOptions _options;
    private FileStream? _stream;
    private int _framesSinceFlush;

    private FrameArchiveWriter(
        string finalPath,
        string partialPath,
        FileStream stream,
        FrameArchiveOptions options
    )
    {
        FinalPath = finalPath;
        PartialPath = partialPath;
        _stream = stream;
        _options = options;
    }

    public string FinalPath { get; }
    public string PartialPath { get; }
    public int FrameCount { get; private set; }
    public long LastTick { get; private set; } = -1;
    public bool IsCompleted { get; private set; }

    internal static FrameArchiveWriter Create(
        string path,
        ReadOnlySpan<byte> content,
        FrameArchiveOptions options
    )
    {
        FrameArchiveFormat.ValidateOptions(options);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Frame archive path cannot be empty.", nameof(path));
        }

        string finalPath = Path.GetFullPath(path);
        string partialPath = finalPath + ".part";
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        if (File.Exists(finalPath) || File.Exists(partialPath))
        {
            throw new IOException($"Frame archive already exists: {finalPath}");
        }

        WireFrameOptions contentOptions = FrameArchiveFormat.ContentWireOptions(options);
        byte[] encodedContent = WireFrame.Encode(content, contentOptions);
        var stream = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan
        );
        try
        {
            Span<byte> header = stackalloc byte[FrameArchiveFormat.ArchiveHeaderSize];
            FrameArchiveFormat.WriteArchiveHeader(header, encodedContent.Length);
            stream.Write(header);
            stream.Write(encodedContent);
            stream.Flush(true);
            return new FrameArchiveWriter(finalPath, partialPath, stream, options);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Append(long tick, bool isCheckpoint, ReadOnlySpan<byte> payload)
    {
        FileStream stream = RequireWritable();
        if (tick < 0 || tick <= LastTick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), "Frame archive ticks must be non-negative and strictly increasing.");
        }

        byte[] encoded = WireFrame.Encode(payload, FrameArchiveFormat.FrameWireOptions(_options));
        Span<byte> recordHeader = stackalloc byte[FrameArchiveFormat.RecordHeaderSize];
        FrameArchiveFormat.WriteRecordHeader(recordHeader, tick, isCheckpoint, encoded.Length);
        stream.Write(recordHeader);
        stream.Write(encoded);

        LastTick = tick;
        FrameCount++;
        _framesSinceFlush++;
        if (_framesSinceFlush >= _options.FlushIntervalFrames)
        {
            stream.Flush(false);
            _framesSinceFlush = 0;
        }
    }

    public void Flush()
    {
        RequireWritable().Flush(true);
        _framesSinceFlush = 0;
    }

    public string Complete()
    {
        FileStream stream = RequireWritable();
        stream.Flush(true);
        stream.Dispose();
        _stream = null;
        File.Move(PartialPath, FinalPath, false);
        IsCompleted = true;
        return FinalPath;
    }

    public void Dispose()
    {
        _stream?.Flush(false);
        _stream?.Dispose();
        _stream = null;
    }

    private FileStream RequireWritable()
    {
        if (_stream == null || IsCompleted)
        {
            throw new InvalidOperationException("Frame archive writer is closed.");
        }
        return _stream;
    }
}

public sealed class FrameArchiveReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly FrameArchiveOptions _options;
    private readonly List<FrameLocation> _frames;

    private FrameArchiveReader(
        string path,
        FileStream stream,
        FrameArchiveOptions options,
        byte[] content,
        List<FrameLocation> frames
    )
    {
        Path = path;
        _stream = stream;
        _options = options;
        Content = content;
        _frames = frames;
    }

    public string Path { get; }
    public byte[] Content { get; }
    public int FrameCount => _frames.Count;
    public long FirstTick => _frames.Count == 0 ? -1 : _frames[0].Tick;
    public long LastTick => _frames.Count == 0 ? -1 : _frames[^1].Tick;
    public int CheckpointCount => _frames.Count(frame => frame.IsCheckpoint);

    internal static FrameArchiveReader Open(string path, FrameArchiveOptions options)
    {
        FrameArchiveFormat.ValidateOptions(options);
        string fullPath = System.IO.Path.GetFullPath(path);
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.RandomAccess
        );
        try
        {
            ScanResult scan = Scan(stream, options, verifyPayloads: true, tolerateInvalidTail: false);
            return new FrameArchiveReader(fullPath, stream, options, scan.Content, scan.Frames);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static FrameArchiveRecovery Recover(string path, FrameArchiveOptions options)
    {
        FrameArchiveFormat.ValidateOptions(options);
        string fullPath = System.IO.Path.GetFullPath(path);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan
        );
        ScanResult scan = Scan(stream, options, verifyPayloads: true, tolerateInvalidTail: true);
        bool truncated = scan.ValidLength != stream.Length;
        if (truncated)
        {
            stream.SetLength(scan.ValidLength);
            stream.Flush(true);
        }
        return new FrameArchiveRecovery(
            scan.ValidLength,
            scan.Frames.Count,
            scan.Frames.Count == 0 ? -1 : scan.Frames[^1].Tick,
            truncated
        );
    }

    public FrameArchiveFrame ReadAt(int index)
    {
        if ((uint)index >= (uint)_frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        FrameLocation frame = _frames[index];
        byte[] encoded = ReadExactlyAt(frame.PayloadOffset, frame.EncodedLength);
        byte[] payload = WireFrame.Decode(encoded, FrameArchiveFormat.FrameWireOptions(_options));
        return new FrameArchiveFrame(frame.Tick, frame.IsCheckpoint, payload);
    }

    public int FindFrameIndex(long tick)
    {
        int low = 0;
        int high = _frames.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_frames[middle].Tick < tick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low < _frames.Count ? low : -1;
    }

    public int FindCheckpointIndex(long tick)
    {
        int low = 0;
        int high = _frames.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_frames[middle].Tick <= tick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (int index = low - 1; index >= 0; index--)
        {
            if (_frames[index].IsCheckpoint)
            {
                return index;
            }
        }
        return -1;
    }

    public IReadOnlyList<FrameArchiveFrame> ReadRange(int startIndex, int count)
    {
        if (startIndex < 0 || startIndex > _frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        int end = Math.Min(_frames.Count, checked(startIndex + count));
        var frames = new List<FrameArchiveFrame>(end - startIndex);
        for (int index = startIndex; index < end; index++)
        {
            frames.Add(ReadAt(index));
        }
        return frames;
    }

    public void Dispose() => _stream.Dispose();

    private byte[] ReadExactlyAt(long offset, int length)
    {
        var value = new byte[length];
        _stream.Position = offset;
        FrameArchiveFormat.ReadExactly(_stream, value);
        return value;
    }

    private static ScanResult Scan(
        FileStream stream,
        FrameArchiveOptions options,
        bool verifyPayloads,
        bool tolerateInvalidTail
    )
    {
        if (stream.Length < FrameArchiveFormat.ArchiveHeaderSize)
        {
            throw new InvalidDataException("Frame archive header is incomplete.");
        }
        stream.Position = 0;
        byte[] archiveHeader = new byte[FrameArchiveFormat.ArchiveHeaderSize];
        FrameArchiveFormat.ReadExactly(stream, archiveHeader);
        int contentLength = FrameArchiveFormat.ReadArchiveHeader(archiveHeader, options);
        if (stream.Length - stream.Position < contentLength)
        {
            throw new InvalidDataException("Frame archive content is incomplete.");
        }
        byte[] encodedContent = new byte[contentLength];
        FrameArchiveFormat.ReadExactly(stream, encodedContent);
        byte[] content = WireFrame.Decode(encodedContent, FrameArchiveFormat.ContentWireOptions(options));

        var frames = new List<FrameLocation>();
        long validLength = stream.Position;
        long lastTick = -1;
        while (stream.Position < stream.Length)
        {
            long recordOffset = stream.Position;
            try
            {
                if (stream.Length - stream.Position < FrameArchiveFormat.RecordHeaderSize)
                {
                    throw new InvalidDataException("Frame archive record header is incomplete.");
                }
                byte[] recordHeader = new byte[FrameArchiveFormat.RecordHeaderSize];
                FrameArchiveFormat.ReadExactly(stream, recordHeader);
                RecordInfo record = FrameArchiveFormat.ReadRecordHeader(recordHeader, options);
                if (record.Tick <= lastTick)
                {
                    throw new InvalidDataException("Frame archive ticks are not strictly increasing.");
                }
                if (stream.Length - stream.Position < record.EncodedLength)
                {
                    throw new InvalidDataException("Frame archive record payload is incomplete.");
                }
                long payloadOffset = stream.Position;
                if (verifyPayloads)
                {
                    byte[] encoded = new byte[record.EncodedLength];
                    FrameArchiveFormat.ReadExactly(stream, encoded);
                    _ = WireFrame.Decode(encoded, FrameArchiveFormat.FrameWireOptions(options));
                }
                else
                {
                    stream.Position += record.EncodedLength;
                }
                frames.Add(new FrameLocation(record.Tick, record.IsCheckpoint, payloadOffset, record.EncodedLength));
                lastTick = record.Tick;
                validLength = stream.Position;
            }
            catch (Exception error) when (tolerateInvalidTail && error is InvalidDataException or EndOfStreamException)
            {
                stream.Position = recordOffset;
                break;
            }
        }
        return new ScanResult(content, frames, validLength);
    }

    private sealed record ScanResult(byte[] Content, List<FrameLocation> Frames, long ValidLength);
    private readonly record struct FrameLocation(long Tick, bool IsCheckpoint, long PayloadOffset, int EncodedLength);
}

internal static class FrameArchiveFormat
{
    internal const int ArchiveHeaderSize = 32;
    internal const int RecordHeaderSize = 32;
    private const byte Version = 1;
    private const byte CheckpointFlag = 1;
    private static readonly byte[] ArchiveMagic = "FWAR"u8.ToArray();
    private static readonly byte[] RecordMagic = "FWAF"u8.ToArray();

    internal static void ValidateOptions(FrameArchiveOptions options)
    {
        if (
            options.CompressionThresholdBytes < 0
            || options.MaxContentBytes <= 0
            || options.MaxFrameBytes <= 0
            || options.MaxEncodedBytes <= 0
            || options.MaxEncodedBytes > int.MaxValue - WireHeaderSize
            || options.FlushIntervalFrames <= 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Frame archive limits are invalid.");
        }
    }

    internal static WireFrameOptions ContentWireOptions(FrameArchiveOptions options) => new(
        options.CompressionThresholdBytes,
        options.MaxContentBytes,
        options.MaxEncodedBytes
    );

    internal static WireFrameOptions FrameWireOptions(FrameArchiveOptions options) => new(
        options.CompressionThresholdBytes,
        options.MaxFrameBytes,
        options.MaxEncodedBytes
    );

    internal static void WriteArchiveHeader(Span<byte> header, int contentLength)
    {
        header.Clear();
        ArchiveMagic.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), ArchiveHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), contentLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), RecordHeaderSize);
        SHA256.HashData(header[..16]).AsSpan(0, 16).CopyTo(header[16..]);
    }

    internal static int ReadArchiveHeader(ReadOnlySpan<byte> header, FrameArchiveOptions options)
    {
        if (header.Length != ArchiveHeaderSize || !header[..4].SequenceEqual(ArchiveMagic))
        {
            throw new InvalidDataException("Frame archive magic is invalid.");
        }
        if (header[4] != Version)
        {
            throw new InvalidDataException($"Frame archive version {header[4]} is unsupported.");
        }
        if (
            header[5] != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)) != ArchiveHeaderSize
            || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != RecordHeaderSize
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header[..16]).AsSpan(0, 16), header[16..])
        )
        {
            throw new InvalidDataException("Frame archive header is invalid.");
        }
        int contentLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (contentLength < WireHeaderSize || contentLength > options.MaxEncodedBytes + WireHeaderSize)
        {
            throw new InvalidDataException($"Frame archive content length is invalid: {contentLength}.");
        }
        return contentLength;
    }

    internal static void WriteRecordHeader(Span<byte> header, long tick, bool isCheckpoint, int encodedLength)
    {
        header.Clear();
        RecordMagic.CopyTo(header);
        header[4] = Version;
        header[5] = isCheckpoint ? CheckpointFlag : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), RecordHeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(8, 8), tick);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), encodedLength);
        SHA256.HashData(header[..24]).AsSpan(0, 8).CopyTo(header[24..]);
    }

    internal static RecordInfo ReadRecordHeader(ReadOnlySpan<byte> header, FrameArchiveOptions options)
    {
        if (header.Length != RecordHeaderSize || !header[..4].SequenceEqual(RecordMagic))
        {
            throw new InvalidDataException("Frame archive record magic is invalid.");
        }
        byte flags = header[5];
        if (
            header[4] != Version
            || (flags & ~CheckpointFlag) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)) != RecordHeaderSize
            || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4)) != 0
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header[..24]).AsSpan(0, 8), header[24..])
        )
        {
            throw new InvalidDataException("Frame archive record header is invalid.");
        }
        long tick = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(8, 8));
        int encodedLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
        if (tick < 0 || encodedLength < WireHeaderSize || encodedLength > options.MaxEncodedBytes + WireHeaderSize)
        {
            throw new InvalidDataException("Frame archive record bounds are invalid.");
        }
        return new RecordInfo(tick, (flags & CheckpointFlag) != 0, encodedLength);
    }

    internal static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Frame archive ended unexpectedly.");
            }
            offset += read;
        }
    }

    private const int WireHeaderSize = 48;
}

internal readonly record struct RecordInfo(long Tick, bool IsCheckpoint, int EncodedLength);
