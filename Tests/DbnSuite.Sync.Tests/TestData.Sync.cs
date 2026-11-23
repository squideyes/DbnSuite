using DbnSuite.Sync.Core;

namespace DbnSuite.Testing;

/// <summary>The part of <see cref="TestData"/> that needs DbnSync's dataset profile.</summary>
internal static partial class TestData
{
    public static SchemaSpec Spec(string name) => GlbxMdp3.Instance.Schemas[name];
}
