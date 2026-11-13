using System.IO.Hashing;
using ZstdSharp;

namespace DbnSuite.Client.Tape;

/// <summary>
/// Builds a tape's body in memory, then saves it whole. The first thing the writer
/// refuses stops it for good, so a tape is either complete and checked or never written.
/// </summary>
public abstract class TapeWriter
{
    private const int InitialCapacity = 1 << 16;
    private const int MostBytesInAnEvent = 64;
    private const int ZstdLevel = 3;

    private readonly TapeKind kind;
    private readonly TapeInfo info;

    private byte[] body = new byte[InitialCapacity];
    private int length;
    private RthMark? rthStart;
    private (long Offset, long Index)? rthEnd;

    private protected TapeWriter(TapeKind kind, TapeInfo info)
    {
        this.kind = kind;
        this.info = info;

        var opening = RthMark.Opening(kind, info.OpenTime, info.BasePrice);

        LastTime = opening.Time;
        CloseUnit = info.CloseTime / TapeHeader.TimeUnitOf(kind);
        Refusal = TapeHeader.CheckNames(info) ?? CheckSession(info);
    }

    /// <summary>Events written so far.</summary>
    public long EventCount { get; private set; }

    /// <summary>The running check over the events written, to compare with a decoded tape.</summary>
    public ulong Checksum { get; private protected set; }

    /// <summary>Why the writer stopped, or null while it is still good.</summary>
    public Error? Refusal { get; private set; }

    private protected long LastTime { get; set; }

    private protected long CloseUnit { get; }

    private protected int BasePrice => info.BasePrice;

    /// <summary>Notes that RTH begins at the next event.</summary>
    public bool MarkRthStart()
    {
        if (Refusal is not null)
            return false;

        if (rthStart is not null)
            return Refuse("RTH has already started.");

        rthStart = Capture() with { BodyOffset = length, EventIndex = EventCount };

        return true;
    }

    /// <summary>Notes that RTH ends before the next event. An unmarked end falls at the session's close.</summary>
    public bool MarkRthEnd()
    {
        if (Refusal is not null)
            return false;

        if (rthStart is null)
            return Refuse("RTH cannot end before it starts.");

        if (rthEnd is not null)
            return Refuse("RTH has already ended.");

        rthEnd = (length, EventCount);

        return true;
    }

    /// <summary>Writes the tape to a stream, header first.</summary>
    public async Task<Result<TapeHeader>> WriteAsync(Stream stream, CancellationToken cancel)
    {
        if (Refusal is not null)
            return Refusal;

        if (EventCount == 0)
            return new Error(TapeErrors.Empty, "A tape with no events is not written.");

        var stored = Stored();
        var header = HeaderFor(stored);
        var front = new byte[TapeHeader.Size];

        header.Write(front);

        await stream.WriteAsync(front, cancel).ConfigureAwait(false);
        await stream.WriteAsync(stored, cancel).ConfigureAwait(false);

        return header;
    }

    /// <summary>Saves the tape through a temporary file, so a half-written tape can never appear at this path.</summary>
    public async Task<Result<TapeHeader>> SaveAsync(string path, CancellationToken cancel)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            Result<TapeHeader> written;

            var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            await using (file.ConfigureAwait(false))
                written = await WriteAsync(file, cancel).ConfigureAwait(false);

            if (written.IsSuccess)
                File.Move(temporary, path, overwrite: true);

            return written;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not save {path}: {problem.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                // The folder itself could not be made, so there is nothing to delete; the error above says why.
            }
        }
    }

    /// <summary>The running state, for a mark. The base class fills in the position.</summary>
    private protected abstract RthMark Capture();

    private protected bool Refuse(string message)
    {
        Refusal ??= new Error(TapeErrors.Refused, message);

        return false;
    }

    /// <summary>Checks that a time can follow the last event and still fall inside the session.</summary>
    private protected bool InSequence(long time)
    {
        if (time < LastTime)
            return Refuse($"Time ran backward: {time} follows {LastTime}.");

        if (time > CloseUnit)
            return Refuse($"Time {time} is after the session's close, {CloseUnit}.");

        return true;
    }

    private protected void BeginEvent(byte control)
    {
        if (body.Length - length < MostBytesInAnEvent)
            Array.Resize(ref body, checked(body.Length * 2));

        body[length++] = control;
        EventCount++;
    }

    private protected void PutByte(byte value) => body[length++] = value;

    private protected void PutVarint(ulong value) => length += Varint.Write(body.AsSpan(length), value);

    private protected void PutZigzag(long value) => PutVarint(Varint.ToZigzag(value));

    private byte[] Stored()
    {
        var plain = body.AsSpan(0, length);

        if (!info.Compress)
            return plain.ToArray();

        using var compressor = new Compressor(ZstdLevel);

        return compressor.Wrap(plain).ToArray();
    }

    private TapeHeader HeaderFor(byte[] stored)
    {
        var end = rthEnd ?? (length, EventCount);

        return new TapeHeader
        {
            Kind = kind,
            Dataset = info.Dataset,
            Symbol = info.Symbol,
            Root = info.Root,
            TradeDate = info.TradeDate,
            TickRaw = info.TickRaw,
            OpenTime = info.OpenTime,
            CloseTime = info.CloseTime,
            BasePrice = info.BasePrice,
            InstrumentId = info.InstrumentId,
            EventCount = EventCount,
            BodyLength = length,
            StoredLength = stored.Length,
            BodyHash = XxHash3.HashToUInt64(stored),
            IsCompressed = info.Compress,
            HasRth = rthStart is not null,
            RthStart = rthStart ?? default,
            RthEndOffset = rthStart is null ? 0 : end.Item1,
            RthEndIndex = rthStart is null ? 0 : end.Item2
        };
    }

    private static Error? CheckSession(TapeInfo info) =>
        info.TickRaw <= 0 || info.OpenTime < 0 || info.CloseTime < info.OpenTime
            ? new Error(TapeErrors.Refused, "The tick must be positive, and the session must open before it closes.")
            : null;
}
