using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using YtPipeline.Cli.Commands;
using YtPipeline.Stages;

// --- Secrets + config -------------------------------------------------------
DotNetEnv.Env.TraversePath().Load();   // .env from cwd or a parent

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

var rootSetting = config["Workspace:Root"];
var workspaceRoot = Path.GetFullPath(
    string.IsNullOrWhiteSpace(rootSetting) ? Directory.GetCurrentDirectory() : rootSetting);
var channelsDir = Path.GetFullPath(
    config["Workspace:ChannelsDir"] is { Length: > 0 } cd ? cd : "channels", workspaceRoot);

// --- Logging ----------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(workspaceRoot, "logs", "ytpipe-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

// --- DI ---------------------------------------------------------------------
var services = new ServiceCollection();
services.AddLogging(b => b.ClearProviders().AddSerilog(dispose: true));
services.AddYtPipeline(config, workspaceRoot, channelsDir);

await using var provider = services.BuildServiceProvider();

// --- CLI --------------------------------------------------------------------
var root = new RootCommand("YtPipeline — 3-channel YouTube automation pipeline.");
root.Subcommands.Add(RunPipelineCommand.Build(provider));
root.Subcommands.Add(GenerateScriptCommand.Build(provider));
root.Subcommands.Add(GenerateImagesCommand.Build(provider));
root.Subcommands.Add(PhoneticsCommand.Build(provider));
root.Subcommands.Add(GenerateVoiceCommand.Build(provider));
root.Subcommands.Add(AssembleVideoCommand.Build(provider));
root.Subcommands.Add(UploadCommand.Build(provider));
root.Subcommands.Add(ApproveCommand.Build(provider));

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Pipeline failed: {Message}", ex.Message);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
