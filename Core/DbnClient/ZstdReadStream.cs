using System.Buffers;
using System.IO.Compression;

namespace DbnClient;

/// <summary>
/// Reads a zstd stream of one or more frames, decompressed, and refuses anything
/// that is not zstd. <see cref="ZstandardStream"/> stops at the end of the last
/// whole frame and ignores whatever follows it, so damage there would pass as a
/// clean end; here, bytes after a frame must start another frame (zstd or
/// skippable), and a stream that ends partway through a frame is damage too.
/// Damage is thrown as <see cref="InvalidDataException"/>. Closes the stream it reads.
/// </summary>
internal sealed class ZstdReadStream(Stream packed) : Stream
{
    private const int InputSize = 1 << 17;

    private readonly ZstandardDecoder decoder = new();
    private readonly byte[] input = ArrayPool<byte>.Shared.Rent(InputSize);
    private int start;
    private int end;
    private bool inFrame;
    private bool ended;
    private bool disposed;

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            var written = Decode(buffer, out var needInput);

            if (!needInput)
                return written;

            Compact();
            Fill(packed.Read(input, end, input.Length - end));
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            var written = Decode(buffer.Span, out var needInput);

            if (!needInput)
                return written;

            Compact();
            Fill(await packed.ReadAsync(input.AsMemory(end), cancellationToken).ConfigureAwait(false));
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            decoder.Dispose();
            ArrayPool<byte>.Shared.Return(input);
            packed.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            decoder.Dispose();
            ArrayPool<byte>.Shared.Return(input);
            await packed.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Decompresses what input there is into <paramref name="output"/>. Returns 0 with
    /// <paramref name="needInput"/> set when it can go no further without more input,
    /// and 0 without it at a clean end: the input ran out between frames.
    /// </summary>
    private int Decode(Span<byte> output, out bool needInput)
    {
        needInput = false;

        while (true)
        {
            if (start == end && !inFrame)
            {
                needInput = !ended;

                return 0;
            }

            var status = decoder.Decompress(input.AsSpan(start, end - start), output, out var consumed, out var written);

            start += consumed;

            if (consumed > 0)
                inFrame = true;

            switch (status)
            {
                case OperationStatus.Done:
                    // The decoder reports Done again and again until it is reset for the next frame.
                    decoder.Reset();
                    inFrame = false;

                    if (written > 0)
                        return written;

                    break;

                case OperationStatus.DestinationTooSmall:
                    return written;

                case OperationStatus.NeedMoreData:
                    if (written > 0)
                        return written;

                    if (ended)
                        throw new InvalidDataException("The zstd data ends partway through a frame.");

                    needInput = true;

                    return 0;

                default:
                    throw new InvalidDataException("The data is not zstd, is damaged, or is followed by bytes that are not zstd.");
            }
        }
    }

    private void Fill(int read)
    {
        if (read == 0)
            ended = true;

        end += read;
    }

    /// <summary>Moves unread input to the front, so the next read has room.</summary>
    private void Compact()
    {
        if (start == 0)
            return;

        input.AsSpan(start, end - start).CopyTo(input);
        end -= start;
        start = 0;
    }
}
