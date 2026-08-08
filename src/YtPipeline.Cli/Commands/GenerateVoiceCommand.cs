using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace YtPipeline.Cli.Commands;

internal static class GenerateVoiceCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var cmd = new Command("voice", "Stage 3 — render the voiceover.");
        cmd.Options.Add(run);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);
            await pipeline.VoiceAsync(r, ct);
            return 0;
        });
        return cmd;
    }
}
