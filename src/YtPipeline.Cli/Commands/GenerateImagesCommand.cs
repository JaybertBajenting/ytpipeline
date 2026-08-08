using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;

namespace YtPipeline.Cli.Commands;

internal static class GenerateImagesCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var dryRun = SharedOptions.DryRun();
        var cmd = new Command("images", "Stage 2 — generate the beat images with Codex.");
        cmd.Options.Add(run);
        cmd.Options.Add(dryRun);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);

            if (parse.GetValue(dryRun))
            {
                var gen = sp.GetRequiredService<CodexCliImageGenerator>();
                var loader = sp.GetRequiredService<ChannelLoader>();
                Console.WriteLine(gen.BuildPrompt(r, pipeline.Runs.LoadScript(r), loader.Load(r.Channel)));
                return 0;
            }

            await pipeline.ImagesAsync(r, ct);
            return 0;
        });
        return cmd;
    }
}
