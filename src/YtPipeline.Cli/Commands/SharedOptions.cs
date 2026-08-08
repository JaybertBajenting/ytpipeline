using System.CommandLine;

namespace YtPipeline.Cli.Commands;

/// <summary>Option instances shared across subcommands.</summary>
internal static class SharedOptions
{
    public static Option<string> Channel() =>
        new("--channel", "-c") { Description = "Channel id (json file name in channels/).", Required = true };

    public static Option<string> Topic() =>
        new("--topic", "-t") { Description = "Video topic.", Required = true };

    public static Option<string> Run() =>
        new("--run", "-r") { Description = "Run id, or 'latest'.", DefaultValueFactory = _ => "latest" };

    public static Option<string> Privacy() =>
        new("--privacy") { Description = "YouTube privacy: private | unlisted | public.", DefaultValueFactory = _ => "private" };

    public static Option<bool> DryRun() =>
        new("--dry-run") { Description = "Print the prompt instead of calling the CLI tool." };
}
