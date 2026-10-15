using System.Text.Json.Nodes;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class SentinelManifestLoaderTests
{
    [Fact]
    public void Load_resolves_an_optional_data_directory()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "sentinel.yaml");
        File.WriteAllText(manifest, """
            service:
              profile: demo
              a2a:
                authentication:
                  mode: anonymous
              data: stores
            sources: []
            queries: []
            reactions:
              - id: notify
            """);

        try
        {
            var loaded = SentinelManifestLoader.Load(manifest);
            Assert.Equal(Path.Combine(directory, "stores"), loaded.DataDirectory);

            File.WriteAllText(manifest, """
                service:
                  profile: demo
                  a2a:
                    authentication:
                      mode: anonymous
                  wal: redb
                sources: []
                queries: []
                reactions:
                  - id: notify
                """);
            var rejected = Assert.Throws<ManifestLoadException>(() => SentinelManifestLoader.Load(manifest));
            Assert.Contains("service.wal", rejected.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_allows_a_curated_query_without_examples()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "sentinel.yaml");
        File.WriteAllText(manifest, """
            plugins:
              - ref: source/postgres
            service:
              profile: demo
              a2a:
                authentication:
                  mode: anonymous
            sources:
              - id: ledger
                kind: postgres
                config:
                  host: 127.0.0.1
            queries:
              - id: omitted
                description: Rows that match.
                cypher: MATCH (n:Item) RETURN n.id AS id
                sources: [ledger]
                reactions: [notify]
              - id: empty
                description: Another match.
                examples: []
                cypher: MATCH (n:Item) RETURN n.id AS id
                sources: [ledger]
                reactions: [notify]
            reactions:
              - id: notify
            """);

        try
        {
            var loaded = SentinelManifestLoader.Load(manifest);
            Assert.Empty(loaded.SafeCatalog.Queries[new QueryId("omitted")].Examples);
            Assert.Empty(loaded.SafeCatalog.Queries[new QueryId("empty")].Examples);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_requires_one_reaction_when_query_authoring_is_enabled()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "sentinel.yaml");
        File.WriteAllText(manifest, """
            service:
              profile: demo
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
    public void Load_rejects_plugin_file_secrets_and_passes_secret_references_through()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "secrets"));
        var manifest = Path.Combine(directory, "sentinel.yaml");
        File.WriteAllText(manifest, """
            service:
              profile: demo
              a2a:
                authentication:
                  mode: anonymous
            sources:
              - id: ledger
                kind: postgres
                config:
                  password:
                    kind: file
                    path: secrets/db
            reactions:
              - id: notify
            """);

        try
        {
            var rejected = Assert.Throws<ManifestLoadException>(() => SentinelManifestLoader.Load(manifest));
            Assert.Contains("not a Drasi config value", rejected.Message, StringComparison.Ordinal);

            var secrets = Path.Combine(directory, "secrets", "secrets.json");
            File.WriteAllText(secrets, """
                {"postgres-password":"example-only"}
                """);
            File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(manifest, """
                plugins:
                  - ref: source/postgres
                  - ref: secret-store/file
                secretStore:
                  kind: file
                  path: secrets/secrets.json
                service:
                  profile: demo
                  a2a:
                    authentication:
                      mode: anonymous
                sources:
                  - id: ledger
                    kind: postgres
                    config:
                      password:
                        kind: Secret
                        name: postgres-password
                queries: []
                reactions:
                  - id: notify
                """);

            var loaded = SentinelManifestLoader.Load(manifest);
            var password = loaded.ExecutableCatalog.Sources["ledger"].Configuration["password"]!;
            Assert.Equal("Secret", password["kind"]!.GetValue<string>());
            Assert.Equal("postgres-password", password["name"]!.GetValue<string>());
            Assert.DoesNotContain("example-only", password.ToJsonString(), StringComparison.Ordinal);
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
