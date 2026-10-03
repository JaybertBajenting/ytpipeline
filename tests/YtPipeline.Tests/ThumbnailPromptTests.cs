using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Process;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// The channel's <c>.thumbnail.md</c> is the authority on what goes in a thumbnail. The prompt
/// builder used to hardcode "do not write the title out — the picture is the hook", which was only
/// ever true of <c>_default.thumbnail.md</c>. For a channel whose rule demands the complete title
/// in a white band, that ban won every time: the band was drawn correctly and left empty, on every
/// thumbnail, with nothing in the logs to say why.
/// </summary>
public sealed class ThumbnailPromptTests
{
    private static CodexCliImageGenerator Generator() => new(
        new ProcessRunner(NullLogger<ProcessRunner>.Instance),
        new CodexOptions(),
        new SettingsStore(Path.GetTempPath()),
        new ProviderHealthStore(Path.GetTempPath()),
        NullLogger<CodexCliImageGenerator>.Instance);

    private static string Build(string thumbnailRule) => Generator().BuildThumbnailPrompt(
        new Run
        {
            RunId = "ch_20260101_000000",
            Channel = "ch",
            Topic = "t",
            WorkspaceRoot = Path.GetTempPath(),
        },
        new ScriptDocument { Title = "How Much Of The Sea Floor We Have Never Mapped" },
        new Channel { Name = "Ch", ThumbnailInstructions = thumbnailRule });

    [Fact]
    public void The_builder_never_bans_lettering_on_its_own_account()
    {
        // The exact instruction that blanked the band. Whatever a rule says, the builder must not
        // reintroduce it — there was no way for a channel to opt out. Other "do NOT" lines in the
        // prompt are about mechanics (don't draw with code, don't ask for confirmation) and stay.
        var prompt = Build("Put the ENTIRE video title, verbatim, in a white band across the top.");

        prompt.Should().NotContain("write the title out");
        prompt.Should().NotContain("the picture is the hook");
    }

    [Fact]
    public void A_rule_demanding_the_full_title_survives_into_the_prompt()
    {
        var prompt = Build("Put the ENTIRE video title, verbatim, in a white band across the top.");

        prompt.Should().Contain("ENTIRE video title, verbatim");
        prompt.Should().Contain("How Much Of The Sea Floor We Have Never Mapped");
        prompt.Should().Contain("complete title verbatim",
            "the builder has to tell the model the rule may legitimately ask for the whole title");
    }

    [Fact]
    public void A_rule_banning_the_full_title_still_gets_its_way()
    {
        // The other direction matters just as much: _default.thumbnail.md and The Old Humans both
        // say "never the full title", and that has to keep working untouched.
        var prompt = Build("Three words maximum. Never a sentence, never the full title.");

        prompt.Should().Contain("Never a sentence, never the full title");
    }

    [Fact]
    public void Composition_advice_for_one_channel_is_not_forced_on_the_others()
    {
        // "Decide what the everyday object is, and what the consequence or goal is" describes the
        // equation composition in _default.thumbnail.md. It has no business in a shared builder.
        var prompt = Build("A solid white band across the top holding the full title.");

        prompt.Should().NotContain("everyday object");
        prompt.Should().NotContain("consequence or goal");
    }

    [Fact]
    public void The_rule_must_be_carried_verbatim_into_the_image_call()
    {
        // codex reaches the image model through an agent that writes its own prompt, so "follow the
        // rule" leaves the rule behind. A real thumbnail came back with a shaded, textured
        // character in a scene whose rule says "flat characters inside a shaded world" — the
        // paraphrase kept the painted background and quietly dropped the flat character.
        var prompt = Build("Flat characters inside a shaded world.");

        prompt.Should().Contain("VERBATIM");
        prompt.Should().Contain("word for word");
        prompt.Should().Contain("=== END THUMBNAIL RULE ===");
        prompt.Should().Contain("sees ONLY the text you pass in that call");
    }

    [Fact]
    public void The_character_versus_background_contrast_is_called_out()
    {
        // The most-dropped constraint gets named explicitly rather than left to survive a summary.
        Build("Flat characters inside a shaded world.")
            .Should().Contain("flat characters in a shaded world");
    }

    [Fact]
    public void The_title_and_the_rule_both_reach_the_model()
    {
        var prompt = Build("RULE-MARKER-TEXT");

        prompt.Should().Contain("RULE-MARKER-TEXT");
        prompt.Should().Contain("How Much Of The Sea Floor We Have Never Mapped");
        prompt.Should().Contain("1280x720");
    }
}
