using System.Runtime.CompilerServices;
using ZstdSharp;

namespace DbnSuite.Client.Dbn;

/// <summary>
/// Reads a DBN stream: the metadata block first, then records one at a time.
/// Each record states its own length, so a record type this reader does not
/// decode is stepped over rather than misread.
/// <para>
/// A stream that cannot be decompressed, ends early or is not DBN is an
/// <see cref="InvalidDataException"/> however the damage shows itself, or
/// whatever the opener asked for instead.
/// </para>
/// </summary>
public sealed class DbnReader : IAsyncDisposable
{
    private const int BufferSize = 1 << 16;
    private const int BlockSize = 1 << 20;

    private readonly Stream stream;
    private readonly Func<string, Exception> damaged;

    private DbnReader(Stream stream, Func<string, Exception> damaged)
    {
        this.stream = stream;
        this.damaged = damaged;
    }

    public DbnMetadata Metadata { get; private set; } = null!;

    /// <summary>Opens a <c>.dbn.zst</c> stream, as DbnSync stores it, and reads its metadata. Disposing the reader disposes the stream.</summary>
    public static Task<DbnReader> OpenAsync(Stream compressed, CancellationToken cancel) =>
        OpenAsync(compressed, detail => new InvalidDataException(detail), cancel);

    /// <param name="damaged">Makes the exception damage is thrown as, from what is wrong.</param>
    internal static async Task<DbnReader> OpenAsync(Stream compressed, Func<string, Exception> damaged, CancellationToken cancel)
    {
        var reader = new DbnReader(new BufferedStream(new DecompressionStream(compressed, leaveOpen: false), BufferSize), damaged);

        try
        {
            reader.Metadata = await DbnMetadata.ReadAsync(reader.stream, cancel).ConfigureAwait(false);

            return reader;
        }
        catch (Exception e)
        {
            await reader.DisposeAsync().ConfigureAwait(false);

            if (IsDamage(e))
                throw damaged(e.Message);

            throw;
        }
    }

    public async IAsyncEnumerable<DbnRecord> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await foreach (var raw in ReadRawAsync(cancel).ConfigureAwait(false))
            yield return DbnRecord.Decode(raw.Span);
    }

    /// <summary>
    /// Each record's bytes, header included, as stored. The memory is reused:
    /// it holds one record only until the next is read. Records are read in
    /// large blocks, so a day of millions costs one await per block, not per record.
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        var buffer = new byte[BlockSize];
        var start = 0;
        var end = 0;

        while (true)
        {
            // Lengths are in 32-bit words and fit a byte, so no record exceeds 1,020 bytes.
            while (end - start > 0 && end - start >= buffer[start] * 4)
            {
                var length = buffer[start] * 4;

                if (length < DbnRecord.HeaderSize)
                    throw damaged($"A record declares {length} bytes, less than its own header.");

                yield return buffer.AsMemory(start, length);

                start += length;
            }

            // Keep the partial record that is left, and fill in behind it.
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;

            var read = await FillAsync(buffer.AsMemory(end), cancel).ConfigureAwait(false);

            if (read == 0)
            {
                if (end > 0)
                    throw damaged($"The stream ends inside a record ({end} bytes left over).");

                yield break;
            }

            end += read;
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();

    private async ValueTask<int> FillAsync(Memory<byte> block, CancellationToken cancel)
    {
        try
        {
            return await stream.ReadAsync(block, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (IsDamage(e))
        {
            throw damaged(e.Message);
        }
    }

    private static bool IsDamage(Exception e) => e is ZstdException or EndOfStreamException or InvalidDataException;
}
