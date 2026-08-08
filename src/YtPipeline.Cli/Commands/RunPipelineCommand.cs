using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace YtPipeline.Cli.Commands;

internal static class RunPipelineCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var channel = SharedOptions.Channel();
        var topic = SharedOptions.Topic();
        var privacy = SharedOptions.Privacy();

        var cmd = new Command("run", "Full run — chains all five stages, pausing at review checkpoints.");
        cmd.Options.Add(channel);
        cmd.Options.Add(topic);
        cmd.Options.Add(privacy);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            await pipeline.RunAllAsync(parse.GetValue(channel)!, parse.GetValue(topic)!, parse.GetValue(privacy)!, ct);
            return 0;
        });
        return cmd;
    }
}
