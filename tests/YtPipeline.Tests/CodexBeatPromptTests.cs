using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Process;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// codex reaches the image model through an agent that writes its own prompt per frame, so unlike
/// gflow — which pastes the whole rule ahead of every single call — the channel's rule only reaches
/// the model if the agent carries it there. When the instruction merely said "follow the STYLE
/// rules above", the agent paraphrased them across a 264-beat session and the constraints drifted
/// away: measured against gflow on the same channel and the same rule file, codex frames came back
/// with twice the empty white (10.5% vs 4.8%), subjects marooned on plain backgrounds that the rule
/// explicitly forbids.
/// </summary>
public sealed class CodexBeatPromptTests
{
    private const string Rule = "Every frame sits on a single flat colour field. The subject fills the frame.";

    private static string Build() => new CodexCliImageGenerator(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new CodexOptions(),
            new SettingsStore(Path.GetTempPath()),
            new ProviderHealthStore(Path.GetTempPath()),
            NullLogger<CodexCliImageGenerator>.Instance)
        .BuildPrompt(
            new Run
            {
                RunId = "ch_20260101_000000",
                Channel = "ch",
                Topic = "t",
                WorkspaceRoot = Path.GetTempPath(),
            },
            new ScriptDocument
            {
                Title = "A Title",
                Beats = { new ScriptBeat { Index = 0, Narration = "Something happens." } },
            },
            new Channel { Name = "Ch", ImageInstructions = Rule });

    [Fact]
    public void The_rule_is_delimited_so_it_can_be_copied_as_a_block()
    {
        var prompt = Build();

        prompt.Should().Contain("=== STYLE RULES");
        prompt.Should().Contain("=== END STYLE RULES ===");
        prompt.Should().Contain(Rule);
    }

    [Fact]
    public void Every_image_call_is_required_to_carry_the_rule_verbatim()
    {
        // "Follow the rules above" is not enough — that is what produced the drift. The instruction
        // has to demand the literal text, because the agent is the only route the rule has to the
        // image model.
        var prompt = Build();

        prompt.Should().Contain("VERBATIM");
        prompt.Should().Contain("word for word");
        prompt.Should().Contain("not summarised");
    }

    [Fact]
    public void The_agent_is_told_why_paraphrasing_loses_the_style()
    {
        // Stating the mechanism matters more than stating the rule: the model cannot see anything
        // except the single call it is given, and each call starts blank.
        Build().Should().Contain("sees ONLY the text you pass in that one call");
    }

    [Fact]
    public void Each_call_is_declared_independent_of_the_last()
    {
        // Drift compounds when the agent assumes the model remembers the previous beat.
        Build().Should().Contain("every call starts blank");
    }

    [Fact]
    public void The_beat_narration_still_reaches_the_prompt()
    {
        Build().Should().Contain("Something happens.");
    }

    [Fact]
    public void Native_landscape_dimensions_are_accepted_without_pixel_edits()
    {
        // Native generation returned a usable 1672x941 frame; requiring exact video dimensions
        // stopped production because code-based resizing is deliberately forbidden here.
        var prompt = Build();

        prompt.Should().Contain("approximately 16:9");
        prompt.Should().Contain("1672x941; do not reject or regenerate");
        prompt.Should().Contain("The final video assembler normalizes frames");
        prompt.Should().Contain("Do not resize, crop or pad it with code");
        prompt.Should().NotContain("Every image must be horizontal 16:9,");
    }

    [Fact]
    public void One_final_frame_allows_native_quality_corrections()
    {
        var prompt = Build();

        prompt.Should().Contain("one accepted FINAL PNG");
        prompt.Should().Contain("regenerate with that native tool");
        prompt.Should().Contain("rejected attempts must stay outside this folder");
        prompt.Should().NotContain("exactly once");
        prompt.Should().NotContain("once per beat");
    }

    [Fact]
    public void Read_only_validation_is_allowed_while_pixel_synthesis_stays_forbidden()
    {
        var prompt = Build();

        prompt.Should().Contain("Read-only inspection of");
        prompt.Should().Contain("file metadata (dimensions, format and opacity) is allowed");
        prompt.Should().Contain("it must not modify image pixels");
        prompt.Should().Contain("Do NOT draw, render, rasterize, or synthesize images with code of ANY kind");
    }
}
