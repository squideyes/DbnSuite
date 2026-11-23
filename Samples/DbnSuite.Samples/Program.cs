// The samples the DbnSuite.Client and DbnTape docs pages quote, as one program:
//
//   dotnet run --project Samples/DbnSuite.Samples -- archive <a folder in the archive's layout>
//   dotnet run --project Samples/DbnSuite.Samples -- tapes <the folder that holds your tapes folder>
//
// Clients/python/tests/fixtures holds synthetic tapes to try the second on.

using DbnSuite.Client.Archive;
using DbnSuite.Client.Tape;
using DbnSuite.Samples;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var cancel = stop.Token;

switch (args)
{
    case ["archive", var folder]:
    {
        using var archive = DbnArchive.Open(new ArchiveOptions { Location = folder }, cacheRoot: null);

        await ArchiveSamples.ListAsync(archive, cancel);
        await ArchiveSamples.ReadAsync(archive, cancel);
        await ArchiveSamples.ExportAsync(archive, cancel);
        await ArchiveSamples.ErrorsAsync(archive, cancel);
        return 0;
    }

    case ["tapes", var folder]:
    {
        Environment.CurrentDirectory = folder;

        await TapeSamples.ReplayAsync(cancel);

        var s1 = await Tape.LoadAsync("tapes/GLBX.MDP3/s1/ES/2026/ES.s1.20261029.tape", cancel);

        if (s1.IsSuccess)
            TapeSamples.Bars(s1.Value);

        await TapeSamples.MonteCarloAsync(cancel);
        return 0;
    }

    default:
        Console.Error.WriteLine("Usage: archive <folder> | tapes <folder that holds tapes/>");
        return 2;
}
