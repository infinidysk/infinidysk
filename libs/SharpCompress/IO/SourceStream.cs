using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SharpCompress.IO;

public partial class SourceStream : Stream, IStreamStack
{
    Stream IStreamStack.BaseStream() => _streams[_stream];

    private readonly List<long> _partStartOffsets = [0];
    private readonly List<long> _partLengths = [];
    private long _knownTotalLength;
    private bool _isDisposed;

    private readonly List<FileInfo> _files;
    private readonly List<Stream> _streams;
    private readonly Func<int, FileInfo?>? _getFilePart;
    private readonly Func<int, Stream?>? _getStreamPart;
    private int _stream;

    public SourceStream(FileInfo file, Func<int, FileInfo?> getPart, ReaderOptions options)
        : this(null, null, file, getPart, options) { }

    public SourceStream(Stream stream, Func<int, Stream?> getPart, ReaderOptions options)
        : this(stream, getPart, null, null, options) { }

    private SourceStream(
        Stream? stream,
        Func<int, Stream?>? getStreamPart,
        FileInfo? file,
        Func<int, FileInfo?>? getFilePart,
        ReaderOptions options
    )
    {
        ReaderOptions = options;
        _files = new List<FileInfo>();
        _streams = new List<Stream>();
        IsFileMode = file != null;
        IsVolumes = false;

        if (!IsFileMode)
        {
            _streams.Add(stream!);
            _getStreamPart = getStreamPart;
            _getFilePart = _ => null;
            if (stream is FileStream fileStream)
            {
                _files.Add(new FileInfo(fileStream.Name));
            }
        }
        else
        {
            _files.Add(file!);
            _streams.Add(_files[0].OpenRead());
            _getFilePart = getFilePart;
            _getStreamPart = _ => null;
        }
        _stream = 0;
        AppendPartCache(0);
    }

    public void LoadAllParts()
    {
        for (var i = 1; SetStream(i); i++) { }
        SetStream(0);
    }

    public bool IsVolumes { get; set; }

    public ReaderOptions ReaderOptions { get; }
    public bool IsFileMode { get; }

    public IEnumerable<FileInfo> Files => _files;
    public IEnumerable<Stream> Streams => _streams;

    private Stream Current => _streams[_stream];

    private long CurrentPartStartOffset => IsVolumes ? 0 : _partStartOffsets[_stream];

    private long RemainingInCurrentPart()
    {
        if (IsVolumes)
        {
            return Current.Length - Current.Position;
        }

        return _partLengths[_stream] - Current.Position;
    }

    private void AppendPartCache(int partIndex)
    {
        var length = _streams[partIndex].Length;
        if (partIndex == _partLengths.Count)
        {
            if (partIndex > 0)
            {
                _partStartOffsets.Add(_knownTotalLength);
            }

            _partLengths.Add(length);
            _knownTotalLength += length;
        }
    }

    private long GetPartEndOffset(int partIndex) =>
        _partStartOffsets[partIndex] + _partLengths[partIndex];

    private void EnsurePartsLoadedForPosition(long position)
    {
        while (GetPartEndOffset(_streams.Count - 1) < position)
        {
            var previousEnd = GetPartEndOffset(_streams.Count - 1);
            if (!LoadStream(_streams.Count))
            {
                break;
            }

            if (GetPartEndOffset(_streams.Count - 1) <= previousEnd)
            {
                throw new ArchiveOperationException(
                    $"Cannot seek to position {position}. Encountered zero-length streams at position {previousEnd}."
                );
            }
        }
    }

    private int FindPartIndexForOffset(long offset)
    {
        for (var i = _partStartOffsets.Count - 1; i >= 0; i--)
        {
            var start = _partStartOffsets[i];
            var length = _partLengths[i];
            if (offset >= start && (length > 0 ? offset < start + length : offset == start))
            {
                return i;
            }
        }

        var lo = 0;
        var hi = _partStartOffsets.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            if (_partStartOffsets[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    public bool LoadStream(int index) //ensure all parts to id are loaded
    {
        while (_streams.Count <= index)
        {
            if (IsFileMode)
            {
                var f = _getFilePart.NotNull("GetFilePart is null")(_streams.Count);
                if (f == null)
                {
                    _stream = _streams.Count - 1;
                    return false;
                }
                _files.Add(f);
                _streams.Add(_files[^1].OpenRead());
            }
            else
            {
                var s = _getStreamPart.NotNull("GetStreamPart is null")(_streams.Count);
                if (s == null)
                {
                    _stream = _streams.Count - 1;
                    return false;
                }
                _streams.Add(s);
                if (s is FileStream stream)
                {
                    _files.Add(new FileInfo(stream.Name));
                }
            }

            AppendPartCache(_streams.Count - 1);
        }
        return true;
    }

    public bool SetStream(int idx) //allow caller to switch part in multipart
    {
        if (LoadStream(idx))
        {
            _stream = idx;
        }

        return _stream == idx;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => IsVolumes ? Current.Length : _knownTotalLength;

    public override long Position
    {
        get => CurrentPartStartOffset + Current.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() => Current.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var total = count;
        var r = -1;

        while (count != 0 && r != 0)
        {
            r = Current.Read(buffer, offset, (int)Math.Min(count, RemainingInCurrentPart()));
            count -= r;
            offset += r;

            if (!IsVolumes && count != 0 && Current.Position == _partLengths[_stream])
            {
                if (!SetStream(_stream + 1))
                {
                    break;
                }

                Current.Seek(0, SeekOrigin.Begin);
                r = -1;
            }
        }

        return total - count;
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var total = buffer.Length;
        var r = -1;

        while (buffer.Length != 0 && r != 0)
        {
            r = Current.Read(
                buffer.Slice(0, (int)Math.Min(buffer.Length, RemainingInCurrentPart()))
            );
            buffer = buffer.Slice(r);

            if (!IsVolumes && buffer.Length != 0 && Current.Position == _partLengths[_stream])
            {
                if (!SetStream(_stream + 1))
                {
                    break;
                }

                Current.Seek(0, SeekOrigin.Begin);
                r = -1;
            }
        }

        return total - buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var pos = Position;
        switch (origin)
        {
            case SeekOrigin.Begin:
                pos = offset;
                break;
            case SeekOrigin.Current:
                pos += offset;
                break;
            case SeekOrigin.End:
                pos = Length + offset;
                break;
        }

        if (IsVolumes)
        {
            Current.Seek(pos, SeekOrigin.Begin);
            return pos;
        }

        EnsurePartsLoadedForPosition(pos);

        if (pos > _knownTotalLength)
        {
            throw new ArchiveOperationException(
                $"Cannot seek to position {pos}. End of stream reached at position {_knownTotalLength}."
            );
        }

        var partIndex = FindPartIndexForOffset(pos);
        SetStream(partIndex);

        var localPos = pos - _partStartOffsets[partIndex];
        if (localPos != Current.Position)
        {
            Current.Seek(localPos, SeekOrigin.Begin);
        }

        return pos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing && !ReaderOptions.LeaveStreamOpen)
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            _streams.Clear();
            _files.Clear();
        }

        _isDisposed = true;
        base.Dispose(disposing);
    }
}
