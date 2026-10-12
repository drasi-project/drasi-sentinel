using Drasi;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

/// <summary>
/// Opens the durable index and state files for one engine process and releases the index lock.
/// Callers pass a manifest directory and an optional absolute data directory. They do not choose
/// store kinds, file names, or shutdown order.
/// </summary>
internal static class DrasiStores
{
    internal const string DirectoryName = "data";

    public static async Task<Engine> OpenAsync(
        string manifestDirectory,
        string? configuredDataDirectory,
        string? secretFilePath,
        string engineId,
        Func<Engine, CancellationToken, Task> bind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentNullException.ThrowIfNull(bind);

        var pluginRoot = Path.GetFullPath(manifestDirectory);
        var dataRoot = ResolveRoot(pluginRoot, configuredDataDirectory);
        ValidatePlacement(pluginRoot, dataRoot, secretFilePath);
        var indexDirectory = Path.Combine(dataRoot, "indexes");
        var walDirectory = Path.Combine(dataRoot, "wal");
        Directory.CreateDirectory(indexDirectory);
        Directory.CreateDirectory(walDirectory);

        var engine = await Engine.CreateAsync(
            engineId,
            new EngineOptions
            {
                IndexStore = new IndexStoreOptions
                {
                    Kind = "rocksdb",
                    Path = indexDirectory,
                },
                StateStore = new StateStoreOptions
                {
                    Kind = "redb",
                    Path = Path.Combine(dataRoot, "state.redb"),
                },
                WalProvider = new WalProviderOptions
                {
                    Kind = "redb",
                    Path = walDirectory,
                },
            },
            cancellationToken);

        try
        {
            await bind(engine, cancellationToken);
            return engine;
        }
        catch (Exception bindFailure)
        {
            try
            {
                await ReleaseAsync(engine);
            }
            catch (Exception releaseFailure)
            {
                throw new AggregateException(bindFailure, releaseFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// Releases the RocksDB lock and asks the engine to shut down. Package 0.2.0
    /// still holds the redb file lock until the process exits.
    /// </summary>
    public static async ValueTask ReleaseAsync(Engine? engine)
    {
        if (engine is null)
            return;

        Exception? shutdownError = null;
        try
        {
            await engine.ShutdownAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            shutdownError = exception;
        }

        try
        {
            await engine.DisposeAsync();
        }
        catch (Exception disposeError)
        {
            throw shutdownError is null
                ? disposeError
                : new AggregateException(shutdownError, disposeError);
        }

        if (shutdownError is not null)
            throw shutdownError;
    }

    internal static string ResolveRoot(string manifestDirectory, string? configuredDataDirectory)
    {
        var pluginRoot = Path.GetFullPath(manifestDirectory);
        if (string.IsNullOrWhiteSpace(configuredDataDirectory))
            return Path.Combine(pluginRoot, DirectoryName);

        if (!Path.IsPathRooted(configuredDataDirectory))
        {
            throw new SentinelException(
                "store_path_invalid",
                "service.data must be an absolute path before the engine opens.");
        }

        return Path.GetFullPath(configuredDataDirectory);
    }

    internal static void ValidatePlacement(string pluginRoot, string dataRoot, string? secretFilePath)
    {
        pluginRoot = Path.GetFullPath(pluginRoot);
        dataRoot = Path.GetFullPath(dataRoot);
        if (Same(dataRoot, pluginRoot) || IsInside(pluginRoot, dataRoot))
        {
            throw new SentinelException(
                "store_path_invalid",
                "The data directory must not contain or equal the manifest directory.");
        }

        if (!string.IsNullOrWhiteSpace(secretFilePath))
        {
            var secret = Path.IsPathRooted(secretFilePath)
                ? Path.GetFullPath(secretFilePath)
                : Path.GetFullPath(secretFilePath, pluginRoot);
            if (Same(secret, dataRoot) || IsInside(secret, dataRoot))
            {
                throw new SentinelException(
                    "store_path_invalid",
                    "The data directory must not contain the secret file.");
            }
        }

        if (File.Exists(dataRoot) && !Directory.Exists(dataRoot))
        {
            throw new SentinelException(
                "store_path_invalid",
                "The data directory path is a file.");
        }

        if (!Directory.Exists(dataRoot))
            return;

        var info = new DirectoryInfo(dataRoot);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SentinelException(
                "store_path_invalid",
                "The data directory must not be a symlink.");
        }
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
}
