namespace DbnSync.Core;

/// <summary>Every dataset DbnSync has a profile for. A dataset not listed here is not supported.</summary>
public static class DatasetProfiles
{
    public static IReadOnlyDictionary<string, IDatasetProfile> All { get; } =
        new IDatasetProfile[] { GlbxMdp3.Instance }.ToDictionary(p => p.Dataset, StringComparer.Ordinal);

    /// <summary>The profile for <paramref name="dataset"/>, matched exactly, or null.</summary>
    public static IDatasetProfile? Find(string dataset) => All.GetValueOrDefault(dataset);
}
