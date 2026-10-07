using Drasi;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: PluginLock <directory> <source-reference> <bootstrap-reference>");
    return 2;
}

var directory = args[0];
var sourceReference = args[1];
var bootstrapReference = args[2];
await using var engine = await Engine.CreateAsync("drasi-sentinel-plugin-lock");
await engine.InstallPluginAsync(sourceReference, directory, load: false);
await engine.InstallPluginAsync(bootstrapReference, directory, load: false);
await engine.WriteLockfileAsync(directory);

Console.WriteLine("Plugin lock updated.");
return 0;
