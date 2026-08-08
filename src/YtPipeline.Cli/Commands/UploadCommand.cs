using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace YtPipeline.Cli.Commands;

internal static class UploadCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var privacy = SharedOptions.Privacy();
        var cmd = new Command("upload", "Stage 5 — upload to YouTube.");
        cmd.Options.Add(run);
        cmd.Options.Add(privacy);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);
            await pipeline.UploadAsync(r, parse.GetValue(privacy)!, ct);
            return 0;
        });
        return cmd;
    }
}
