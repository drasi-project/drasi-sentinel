using System.Text.Json.Nodes;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class ExampleManifestTests
{
    [Theory]
    [InlineData("invoice-overdue")]
    [InlineData("accounting-local-demo")]
    [InlineData("curbside-local-demo")]
    public void Load_accepts_each_example_with_private_fixture_files(string exampleName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string endpointName = "AZURE_OPENAI_ENDPOINT";
        const string modelName = "AZURE_OPENAI_MODEL";
        var previousEndpoint = Environment.GetEnvironmentVariable(endpointName);
        var previousModel = Environment.GetEnvironmentVariable(modelName);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable(endpointName, "https://models.example.test/");
            Environment.SetEnvironmentVariable(modelName, "test-model");
            Directory.CreateDirectory(Path.Combine(directory, "secrets"));
            var sourceDirectory = Path.Combine(
                FindRepositoryRoot(),
                "examples",
                exampleName);
            File.Copy(
                Path.Combine(sourceDirectory, "sentinel.yaml"),
                Path.Combine(directory, "sentinel.yaml"));
            CopyPrivateFixture(
                sourceDirectory,
                directory,
                "secrets.json.example",
                "secrets.json");

            var manifest = SentinelManifestLoader.Load(Path.Combine(directory, "sentinel.yaml"));
            var localDemo = exampleName is "accounting-local-demo" or "curbside-local-demo";

            Assert.Equal("test-model", manifest.Agent!.Model);
            if (exampleName == "curbside-local-demo")
            {
                Assert.Empty(manifest.SafeCatalog.Queries);
                Assert.Equal(
                    ["source/postgres", "bootstrap/postgres", "source/mysql", "bootstrap/mysql", "secret-store/file"],
                    manifest.Plugins);
            }
            else
            {
                Assert.NotEmpty(manifest.SafeCatalog.Queries);
                Assert.Equal(["source/postgres", "bootstrap/postgres", "secret-store/file"], manifest.Plugins);
            }
            Assert.Equal("file", manifest.SecretStore?.Kind);
            Assert.True(Path.IsPathRooted(manifest.SecretStore!.Configuration["path"]!.GetValue<string>()));
            Assert.All(
                manifest.ExecutableCatalog.Sources.Values,
                source => Assert.Equal(
                    "Secret",
                    source.Configuration["password"]!["kind"]!.GetValue<string>()));
            Assert.NotEmpty(manifest.SafeCatalog.Reactions);
            Assert.Equal(localDemo ? ServiceProfile.Demo : ServiceProfile.Production, manifest.Profile);
            Assert.IsType(
                localDemo ? typeof(A2aAnonymousManifest) : typeof(A2aJwtManifest),
                manifest.A2aAuthentication);
            Assert.Equal(
                exampleName == "accounting-local-demo" ? new Uri("http://127.0.0.1:8082/") : null,
                manifest.WebhookUrl);
            Assert.Equal(localDemo, manifest.QueryAuthoringEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(endpointName, previousEndpoint);
            Environment.SetEnvironmentVariable(modelName, previousModel);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void CopyPrivateFixture(
        string sourceDirectory,
        string destinationDirectory,
        string fixtureName,
        string secretName)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Example fixture validation requires Unix file permissions.");
        }

        var destination = Path.Combine(destinationDirectory, "secrets", secretName);
        File.Copy(
            Path.Combine(sourceDirectory, "secrets", fixtureName),
            destination);
        File.SetUnixFileMode(
            destination,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Drasi.Sentinel.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
