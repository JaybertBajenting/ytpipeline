using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace YtPipeline.Cli.Commands;

internal static class AssembleVideoCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var cmd = new Command("video", "Stage 4 — assemble the final mp4.");
        cmd.Options.Add(run);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);
            await pipeline.VideoAsync(r, ct);
            return 0;
        });
        return cmd;
    }
}
