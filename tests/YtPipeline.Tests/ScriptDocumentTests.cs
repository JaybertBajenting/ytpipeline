using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Validation;
using YtPipeline.Stages.Script;
using Xunit;

namespace YtPipeline.Tests;

public sealed class ScriptDocumentTests
{
    private static ScriptDocument Valid() => new()
    {
        Title = "How DNS works",
        Description = "A short explainer.",
        Tags = { "dns" },
        Beats =
        {
            new ScriptBeat { Index = 0, Narration = "Hook.", ImagePrompt = "wizard" },
            new ScriptBeat { Index = 1, Narration = "Answer.", ImagePrompt = "phone book" },
        },
    };

    [Fact]
    public void Validator_passes_a_well_formed_script()
    {
        new ScriptDocumentValidator().Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_rejects_empty_beats()
    {
        var doc = Valid();
        doc.Beats.Clear();
        new ScriptDocumentValidator().Validate(doc).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_rejects_duplicate_beat_indices()
    {
        var doc = Valid();
        doc.Beats[1].Index = 0;
        new ScriptDocumentValidator().Validate(doc).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ComposeNarration_joins_beats_when_full_narration_blank()
    {
        Valid().ComposeNarration().Should().Be("Hook. Answer.");
    }

    [Theory]
    [InlineData("here is your script:\n{\"title\":\"T\",\"description\":\"D\",\"beats\":[{\"index\":0,\"narration\":\"n\",\"imagePrompt\":\"p\"}]}")]
    [InlineData("```json\n{\"title\":\"T\",\"description\":\"D\",\"beats\":[{\"index\":0,\"narration\":\"n\",\"imagePrompt\":\"p\"}]}\n```")]
    public void ExtractScript_pulls_json_out_of_prose_or_fences(string raw)
    {
        var doc = ClaudeCliScriptGenerator.ExtractScript(raw);
        doc.Title.Should().Be("T");
        doc.Beats.Should().ContainSingle();
    }

    [Fact]
    public void ExtractScript_handles_braces_inside_strings()
    {
        var raw = "{\"title\":\"a } b\",\"description\":\"D\",\"beats\":[{\"index\":0,\"narration\":\"n\",\"imagePrompt\":\"p\"}]}";
        ClaudeCliScriptGenerator.ExtractScript(raw).Title.Should().Be("a } b");
    }
}
