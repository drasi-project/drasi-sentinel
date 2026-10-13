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
    internal const string IndexDirectoryName = "indexes";
    internal const string ProbeQueryPrefix = "query-probe-";

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
        var indexDirectory = Path.Combine(dataRoot, IndexDirectoryName);
        var walDirectory = Path.Combine(dataRoot, "wal");
        Directory.CreateDirectory(indexDirectory);
        Directory.CreateDirectory(walDirectory);
        DeleteLeftoverProbeIndexes(indexDirectory);

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

    internal static string IndexDirectory(string manifestDirectory, string? configuredDataDirectory) =>
        Path.Combine(ResolveRoot(manifestDirectory, configuredDataDirectory), IndexDirectoryName);

    /// <summary>
    /// Probe ids are <c>query-probe-</c> plus 32 lowercase hex digits. Committed
    /// queries use <c>query-{catalog id}</c> and are not deleted here.
    /// </summary>
    internal static bool IsProbeQueryId(string queryId)
    {
        if (queryId.Length != ProbeQueryPrefix.Length + 32
            || !queryId.StartsWith(ProbeQueryPrefix, StringComparison.Ordinal))
            return false;

        for (var index = ProbeQueryPrefix.Length; index < queryId.Length; index++)
        {
            var character = queryId[index];
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    internal static void DeleteProbeIndex(string indexDirectory, string queryId)
    {
        if (!IsProbeQueryId(queryId))
        {
            throw new InvalidOperationException(
                $"Refusing to delete index '{queryId}' because it is not a probe.");
        }

        var root = Path.GetFullPath(indexDirectory);
        var target = Path.Combine(root, queryId);
        if (!Directory.Exists(target))
            return;

        var info = new DirectoryInfo(target);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to delete probe index symlink '{queryId}'.");
        }

        var full = Path.GetFullPath(target);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison)
            || !string.Equals(Path.GetFileName(full), queryId, PathComparison))
        {
            throw new InvalidOperationException(
                $"Probe index '{queryId}' is outside the index directory.");
        }

        Directory.Delete(full, recursive: true);
    }

    internal static void DeleteLeftoverProbeIndexes(string indexDirectory)
    {
        if (!Directory.Exists(indexDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(indexDirectory))
        {
            var name = Path.GetFileName(directory);
            if (!IsProbeQueryId(name))
                continue;

            try
            {
                DeleteProbeIndex(indexDirectory, name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SentinelException(
                    "probe_index_retained",
                    $"The probe index '{name}' was not deleted.",
                    exception);
            }
        }
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
