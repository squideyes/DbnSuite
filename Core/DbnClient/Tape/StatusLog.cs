using System.Buffers.Binary;
using DbnClient.Archive;
using DbnClient.Dbn;

namespace DbnClient.Tape;

/// <summary>Reads the status records a trade date's tapes need.</summary>
internal static class StatusLog
{
    private const string Schema = "status";
    private const char Yes = 'Y';
    private const char No = 'N';
    private const long MaxLead = 60_000_000_000;

    public static StatusPoint Point(ReadOnlySpan<byte> raw)
    {
        var trading = (char)raw[30];

        return new StatusPoint(
            (long)BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(raw[24..]),
            trading == Yes ? true : trading == No ? false : null);
    }

    /// <summary>
    /// How far the receive clock ran ahead of the exchange over a trade date, in nanoseconds.
    /// Bars are cut on that clock, and a record cannot really arrive before it happened.
    /// </summary>
    public static async Task<long> ClockLeadAsync(DbnArchive archive, string root, DateOnly tradeDate, CancellationToken cancel)
    {
        var lead = 0L;

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(Schema, root, day, cancel).ConfigureAwait(false);

            // Only a day with no file or no data has no clock to read; any other failure must be heard.
            if (!opened.IsSuccess && opened.Error!.Code is not (Errors.NotStored or Errors.NoData))
                throw new ArchiveException(opened.Error);

            if (!opened.IsSuccess)
                continue;

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != StatusRecord.Type)
                        continue;

                    var ahead = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.Span[8..]) - (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.Span[16..]);

                    if (ahead < MaxLead)
                        lead = Math.Max(lead, ahead);
                }
            }
        }

        return lead;
    }

    /// <summary>Every instrument's status over a trade date's two UTC files, oldest file first.</summary>
    public static async Task<Result<Dictionary<uint, List<StatusPoint>>>> ReadAsync(DbnArchive archive, string root, DateOnly tradeDate, CancellationToken cancel)
    {
        var points = new Dictionary<uint, List<StatusPoint>>();

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(Schema, root, day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
                return opened.Error!;

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != StatusRecord.Type)
                        continue;

                    var id = DbnRecord.InstrumentOf(raw.Span);

                    if (!points.TryGetValue(id, out var list))
                        points[id] = list = [];

                    list.Add(Point(raw.Span));
                }
            }
        }

        return points;
    }
}
