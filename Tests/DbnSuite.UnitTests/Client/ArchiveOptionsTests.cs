using DbnSuite.Client.Archive;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class ArchiveOptionsTests
{
    private const string Secret = "not-for-logs";

    [Fact]
    public void Printing_options_leaves_the_keys_out()
    {
        var options = new ArchiveOptions { Location = "https://s3.example.com", SecretKey = Secret };

        options.ToString().ShouldNotContain(Secret);
    }
}
