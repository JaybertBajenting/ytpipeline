using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace YtPipeline.Cli.Commands;

internal static class ApproveCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var stage = new Option<string>("--stage")
        {
            Description = "Checkpoint to approve: script | video.",
            Required = true,
        };

        var cmd = new Command("approve", "Approve a review checkpoint so the pipeline can continue.");
        cmd.Options.Add(run);
        cmd.Options.Add(stage);

        cmd.SetAction(parse =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);
            pipeline.Approve(r, parse.GetValue(stage)!);
            return 0;
        });
        return cmd;
    }
}
