using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Drasi.Sentinel.Tests;

public sealed class WatchRestartBattleTests
{
    [Fact(Timeout = 480000)]
    public async Task Restart_delivers_a_change_that_arrived_while_the_process_was_down()
    {
        var container = "sentinel-reattach-" + Guid.NewGuid().ToString("N")[..12];
        var directory = Path.Combine(Path.GetTempPath(), "sentinel-reattach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = FreePort();
        try
        {
            await Docker(
                "run", "-d", "--name", container,
                "-e", "POSTGRES_USER=sentinel",
                "-e", "POSTGRES_PASSWORD=sentinel-demo",
                "-e", "POSTGRES_DB=sentinel",
                "-p", $"127.0.0.1:{port}:5432",
                "postgres:16",
                "postgres", "-c", "wal_level=logical", "-c", "max_replication_slots=4", "-c", "max_wal_senders=4");
            await WaitForPostgres(container);
            await Psql(container, "CREATE TABLE items (item_id text PRIMARY KEY, paid boolean NOT NULL);");
            await Psql(container, "ALTER TABLE items REPLICA IDENTITY FULL;");
            await Psql(container, "CREATE PUBLICATION drasi_reattach_publication FOR TABLE items;");
            await Psql(container, "SELECT pg_create_logical_replication_slot('drasi_reattach_slot', 'pgoutput');");

            var harness = await BuildHarness();
            await RunHarness(harness, "arm", directory, port, container);
            await Psql(container, "INSERT INTO items (item_id, paid) VALUES ('item-2', true);");
            await RunHarness(harness, "resume", directory, port, container);

            var journal = await ReadJournal(directory);
            Assert.Equal(1, journal.Count(item => item.Contains("item-1", StringComparison.Ordinal)));
            Assert.Equal(1, journal.Count(item => item.Contains("item-2", StringComparison.Ordinal)));
        }
        finally
        {
            try
            {
                await Docker("rm", "-f", container);
            }
            catch (Exception)
            {
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<string> BuildHarness()
    {
        var project = Path.Combine(FindRoot(), "tests", "Drasi.Sentinel.WatchRestart", "Drasi.Sentinel.WatchRestart.csproj");
        var built = await Run("dotnet", "build", project, "-c", "Debug", "--nologo");
        if (built.ExitCode != 0)
            throw new InvalidOperationException(built.Text);
        var dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", "Drasi.Sentinel.WatchRestart.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("Harness was not built.", dll);
        return dll;
    }

    private static async Task RunHarness(string dll, string phase, string directory, int port, string container)
    {
        var ready = Path.Combine(directory, "ready");
        if (File.Exists(ready))
            File.Delete(ready);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the restart harness.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (phase == "arm")
        {
            var readyDeadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (!File.Exists(ready) && DateTimeOffset.UtcNow < readyDeadline && !process.HasExited)
                await Task.Delay(200);
            if (!File.Exists(ready))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Harness did not become ready.{Environment.NewLine}{await stdout}{await stderr}");
            }

            await Psql(container, "INSERT INTO items (item_id, paid) VALUES ('item-1', true);");
        }

        var exitDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!process.HasExited && DateTimeOffset.UtcNow < exitDeadline)
            await Task.Delay(200);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Harness {phase} timed out.{Environment.NewLine}{await stdout}{await stderr}");
        }

        var text = (await stdout) + (await stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Harness {phase} exited {process.ExitCode}.{Environment.NewLine}{text}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port is 5432 or 5433 ? FreePort() : port;
    }

    private static async Task WaitForPostgres(string container)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ready = await Run("docker", "exec", container, "pg_isready", "-U", "sentinel", "-d", "sentinel");
            if (ready.ExitCode == 0)
                return;
            await Task.Delay(500);
        }

        throw new TimeoutException("PostgreSQL did not become ready.");
    }

    private static Task Psql(string container, string sql) =>
        Docker("exec", container, "psql", "-v", "ON_ERROR_STOP=1", "-U", "sentinel", "-d", "sentinel", "-c", sql);

    private static async Task Docker(params string[] args)
    {
        var result = await Run("docker", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Text);
    }

    private static async Task<IReadOnlyList<string>> ReadJournal(string directory)
    {
        var path = Path.Combine(directory, "data", "journal.sqlite");
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM sentinel_task_events ORDER BY sequence";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Drasi.Sentinel.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static async Task<RunResult> Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var text = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, text);
    }

    private sealed record RunResult(int ExitCode, string Text);
}
