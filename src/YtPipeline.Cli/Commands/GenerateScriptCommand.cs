using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Script;

namespace YtPipeline.Cli.Commands;

internal static class GenerateScriptCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var channel = SharedOptions.Channel();
        var topic = SharedOptions.Topic();
        var dryRun = SharedOptions.DryRun();

        var cmd = new Command("script", "Stage 1 — generate the script (creates a new run).");
        cmd.Options.Add(channel);
        cmd.Options.Add(topic);
        cmd.Options.Add(dryRun);

        cmd.SetAction(async (parse, ct) =>
        {
            var channelId = parse.GetValue(channel)!;
            var topicText = parse.GetValue(topic)!;

            if (parse.GetValue(dryRun))
            {
                var loader = sp.GetRequiredService<ChannelLoader>();
                var gen = sp.GetRequiredService<ClaudeCliScriptGenerator>();
                Console.WriteLine(gen.BuildPrompt(loader.Load(channelId), topicText));
                return 0;
            }

            var pipeline = sp.GetRequiredService<Pipeline>();
            var run = pipeline.Runs.Create(channelId, topicText);
            await pipeline.ScriptAsync(run, ct);
            Console.WriteLine(run.RunId);
            return 0;
        });
        return cmd;
    }
}
