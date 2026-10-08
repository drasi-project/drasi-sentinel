using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class SentinelManifestLoaderTests
{
    [Fact]
    public void Load_requires_one_reaction_when_query_authoring_is_enabled()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "secrets"));
        var secret = Path.Combine(directory, "secrets", "db");
        File.WriteAllText(secret, "Host=127.0.0.1;Username=sentinel;Password=secret;Database=sentinel\n");
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var manifest = Path.Combine(directory, "sentinel.yaml");
        File.WriteAllText(manifest, """
            service:
              profile: demo
              database:
                kind: file
                path: secrets/db
              a2a:
                authentication:
                  mode: anonymous
              query_authoring:
                enabled: true
            sources:
              - id: ledger
                kind: postgres
                config:
                  host: 127.0.0.1
            queries:
              - id: sample
                description: Sample.
                examples: ["Show the sample."]
                cypher: MATCH (n:Item) RETURN n.id AS id
                sources: [ledger]
                reactions: [notify]
            reactions:
              - id: notify
              - id: other
            """);

        try
        {
            var error = Assert.Throws<ManifestLoadException>(() => SentinelManifestLoader.Load(manifest));
            Assert.Contains("exactly one reaction", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
