using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class SentinelManifestLoaderTests
{
    [Fact]
    public void Load_rejects_legacy_destination_configuration()
    {
        var manifest = Path.Combine(FindRoot(), "tests", "legacy-destination-test.yaml");
        try
        {
            File.WriteAllText(manifest, "destinations: []\n");
            Assert.Throws<ManifestLoadException>(() => SentinelManifestLoader.Load(manifest));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Drasi.Sentinel.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
