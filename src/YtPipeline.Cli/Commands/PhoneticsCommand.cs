using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using YtPipeline.Stages.Orchestration;
using YtPipeline.Stages.Script;

namespace YtPipeline.Cli.Commands;

/// <summary>
/// Utility — re-run the TTS phonetic respelling pass over an existing run's script.json and
/// save it back (populates each beat's NarrationSpoken). Normally this runs automatically as
/// part of the script stage; this command lets you (re)apply it to a hand-made or older script.
/// </summary>
internal static class PhoneticsCommand
{
    public static Command Build(IServiceProvider sp)
    {
        var run = SharedOptions.Run();
        var cmd = new Command("phonetics", "Apply the TTS phonetic respelling pass to a run's script.");
        cmd.Options.Add(run);

        cmd.SetAction(async (parse, ct) =>
        {
            var pipeline = sp.GetRequiredService<Pipeline>();
            var cleaner = sp.GetRequiredService<PhoneticNarrationCleaner>();
            var r = pipeline.Runs.Resolve(parse.GetValue(run)!);

            var script = pipeline.Runs.LoadScript(r);
            await cleaner.ApplyAsync(script, ct);
            pipeline.Runs.SaveScript(r, script);
            return 0;
        });
        return cmd;
    }
}
