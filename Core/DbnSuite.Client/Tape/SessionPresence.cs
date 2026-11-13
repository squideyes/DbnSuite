using System.Buffers.Binary;
using DbnSuite.Client.Dbn;

namespace DbnSuite.Client.Tape;

/// <summary>
/// Whether a contract's <c>mbo</c> records open its session the way <see cref="ContractRun"/>
/// opens it: a record outside a snapshot, at or after the open, before any at or after the
/// close. Resting orders seen only in snapshots do not, so a contract that never traded
/// rightly has no tape.
/// </summary>
internal sealed class SessionPresence(SessionDay session)
{
    private bool decided;

    public bool Opened { get; private set; }

    /// <summary>Sees one <c>mbo</c> record of the contract, as stored.</summary>
    public void Feed(ReadOnlySpan<byte> record)
    {
        if (decided)
            return;

        var action = (char)record[38];

        if ((record[36] & MboRecord.SnapshotFlag) != 0 || action == 'R')
            return;

        var time = (long)BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);

        if (time >= session.Close)
        {
            decided = true;
        }
        else if (time >= session.Open)
        {
            decided = true;
            Opened = true;
        }
    }
}
