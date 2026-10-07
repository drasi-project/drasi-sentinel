using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class ExampleManifestTests
{
    [Theory]
    [InlineData("invoice-overdue")]
    [InlineData("accounting-local-demo")]
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
                "postgres.connection-string.example",
                "postgres.connection-string");
            if (exampleName == "accounting-local-demo")
            {
                CopyPrivateFixture(
                    sourceDirectory,
                    directory,
                    "postgres.password.example",
                    "postgres.password");
            }
            else
            {
                CopyPrivateFixture(
                    sourceDirectory,
                    directory,
                    "postgres.cdc-password.example",
                    "postgres.cdc-password");
                CopyPrivateFixture(
                    sourceDirectory,
                    directory,
                    "postgres.cdc-port.example",
                    "postgres.cdc-port");
            }
            if (exampleName == "invoice-overdue")
            {
                CopyPrivateFixture(
                    sourceDirectory,
                    directory,
                    "a2a-public-key.pem.example",
                    "a2a-public-key.pem");
            }

            var manifest = SentinelManifestLoader.Load(Path.Combine(directory, "sentinel.yaml"));

            Assert.Equal("test-model", manifest.Agent!.Model);
            Assert.NotEmpty(manifest.SafeCatalog.Queries);
            Assert.NotEmpty(manifest.SafeCatalog.Reactions);
            Assert.Equal(
                exampleName == "accounting-local-demo" ? ServiceProfile.Demo : ServiceProfile.Production,
                manifest.Profile);
            Assert.IsType(
                exampleName == "accounting-local-demo"
                    ? typeof(A2aAnonymousManifest)
                    : typeof(A2aJwtManifest),
                manifest.A2aAuthentication);
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
