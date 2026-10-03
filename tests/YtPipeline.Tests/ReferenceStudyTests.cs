using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Core.Validation;
using YtPipeline.Stages.Images;
using YtPipeline.Stages.Process;
using YtPipeline.Stages.Script;

namespace YtPipeline.Tests;

public sealed class ReferenceStudyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yt_reference_" + Guid.NewGuid().ToString("N"));

    public ReferenceStudyTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sources"));
        File.WriteAllText(Path.Combine(_dir, "sources", "transcript.txt"), "A real source sentence continues across several visual cuts.");
        File.WriteAllBytes(Path.Combine(_dir, "sources", "frame.png"), [137, 80, 78, 71]);
        File.WriteAllBytes(Path.Combine(_dir, "sources", "thumb.jpg"), [255, 216, 255]);
    }

    private static Channel StudiedChannel() => new()
    {
        Id = "studied",
        SecondsPerImage = 4,
        ReferenceStudy = new()
        {
            SourceUrl = "https://www.youtube.com/@Reference",
            TranscriptPaths = ["sources/transcript.txt"],
            ImagePaths = ["sources/frame.png"],
            ThumbnailPaths = ["sources/thumb.jpg"],
        },
    };

    private Run Run() => new()
    {
        RunId = "studied_test", Channel = "studied", Topic = "An original topic", WorkspaceRoot = _dir,
    };

    [Fact]
    public void Script_prompt_contains_actual_source_text_from_the_configured_workspace()
    {
        var generator = new ClaudeCliScriptGenerator(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new ScriptDocumentValidator(), new ClaudeOptions(), new SettingsStore(_dir),
            NullLogger<ClaudeCliScriptGenerator>.Instance);

        var prompt = generator.BuildPrompt(StudiedChannel(), "An original topic");

        prompt.Should().Contain(File.ReadAllText(Path.Combine(_dir, "sources", "transcript.txt")));
        prompt.Should().Contain("not factual sources for a different topic");
        prompt.Should().Contain("not sentence length");
    }

    [Fact]
    public void Missing_transcript_blocks_generation_instead_of_sending_only_a_source_URL()
    {
        File.Delete(Path.Combine(_dir, "sources", "transcript.txt"));
        var act = () => ReferenceStudySources.ScriptContext(StudiedChannel(), _dir);

        act.Should().Throw<FileNotFoundException>().WithMessage("*required reference*transcript.txt*");
    }

    [Fact]
    public void Frame_and_thumbnail_sources_are_resolved_separately_and_must_exist()
    {
        var channel = StudiedChannel();
        ReferenceStudySources.Images(channel, _dir).Should().Equal(Path.Combine(_dir, "sources", "frame.png"));
        ReferenceStudySources.Images(channel, _dir, thumbnail: true).Should().Equal(Path.Combine(_dir, "sources", "thumb.jpg"));

        File.Delete(Path.Combine(_dir, "sources", "thumb.jpg"));
        var act = () => ReferenceStudySources.Images(channel, _dir, thumbnail: true);
        act.Should().Throw<FileNotFoundException>();
        ReferenceStudySources.Images(channel, _dir).Should().ContainSingle();
    }

    [Fact]
    public void Empty_source_lists_fail_for_opted_in_channels_but_other_channels_need_no_sources()
    {
        var studied = StudiedChannel();
        studied.ReferenceStudy!.ImagePaths.Clear();
        var act = () => ReferenceStudySources.Images(studied, _dir);
        act.Should().Throw<InvalidOperationException>().WithMessage("*imagePaths is empty*");

        ReferenceStudySources.Images(new Channel(), "no-such-workspace").Should().BeEmpty();
        ReferenceStudySources.ScriptContext(new Channel(), "no-such-workspace").Should().BeEmpty();
    }

    [Fact]
    public void Codex_attaches_real_images_and_requires_them_again_on_each_native_tool_call()
    {
        var generator = new CodexCliImageGenerator(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new CodexOptions(), new SettingsStore(_dir), new ProviderHealthStore(_dir),
            NullLogger<CodexCliImageGenerator>.Instance);
        var channel = StudiedChannel();
        var refs = ReferenceStudySources.Images(channel, _dir);
        var args = CodexCliImageGenerator.BuildGenerationArgs(["exec", "-"], refs);
        args.Should().Equal("exec", "--image", refs[0], "-");

        var prompt = generator.BuildPrompt(Run(), new ScriptDocument
        {
            Beats = [new ScriptBeat { Index = 12, Narration = "A later beat still needs source evidence." }],
        }, channel, [12]);
        prompt.Should().Contain("referenced_image_paths").And.Contain("EVERY individual frame/thumbnail call");
        prompt.Should().Contain(System.Text.Json.JsonSerializer.Serialize(refs));

        var thumbnail = generator.BuildThumbnailPrompt(Run(), new ScriptDocument { Title = "Topic" }, channel);
        thumbnail.Should().Contain("thumb.jpg").And.NotContain("frame.png");

        prompt.Should().NotContain("uses NO reference images", "a studied channel is exactly the one that gets them");
        thumbnail.Should().NotContain("uses NO reference images");
    }

    [Fact]
    public void A_channel_without_references_is_told_to_attach_nothing_and_gets_no_source_wording()
    {
        // The Old Humans: its look is its rule text, drawn from studying Ink Explainer. Barely An
        // Expert's reference wording used to reach it, and Codex attached its avatar and banner on its own.
        var generator = new CodexCliImageGenerator(new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            new CodexOptions(), new SettingsStore(_dir), new ProviderHealthStore(_dir),
            NullLogger<CodexCliImageGenerator>.Instance);
        var plain = new Channel { Id = "the-old-humans", Name = "The Old Humans", ImageInstructions = "Ink rule." };

        ReferenceStudySources.Images(plain, _dir).Should().BeEmpty();
        CodexCliImageGenerator.BuildGenerationArgs(["exec", "-"], ReferenceStudySources.Images(plain, _dir))
            .Should().Equal("exec", "-");

        var prompt = generator.BuildPrompt(Run(), new ScriptDocument
        {
            Beats = [new ScriptBeat { Index = 0, Narration = "Fire changed everything." }],
        }, plain);
        var thumbnail = generator.BuildThumbnailPrompt(Run(), new ScriptDocument { Title = "Topic" }, plain);

        foreach (var p in new[] { prompt, thumbnail })
        {
            p.Should().Contain("uses NO reference images").And.Contain("Never pass referenced_image_paths");
            p.Should().NotContain("ACTUAL SOURCE IMAGES").And.NotContain("source-matching")
                .And.NotContain("required source images");
        }
    }

    [Theory]
    [InlineData("", "codex", "codex")]
    [InlineData("gflow", "codex", "gflow")]
    [InlineData("codex", "gflow", "codex")]
    public void A_channel_that_names_its_generator_beats_settings(string channelProvider, string settings, string expected) =>
        ImageGeneratorRouter.ChannelOr(new Channel { ImageProvider = channelProvider }, settings).Should().Be(expected);

    [Fact]
    public void Codex_settings_replace_startup_model_and_effort_without_losing_references()
    {
        var args = CodexCliImageGenerator.BuildGenerationArgs(
            ["exec", "--model", "gpt-6-astra", "-c", "model_reasoning_effort=\"low\"", "-"],
            ["source.png"], "gpt-5.6-sol", "high");
        args.Should().Equal("exec", "--model", "gpt-5.6-sol", "-c",
            "model_reasoning_effort=\"high\"", "--image", "source.png", "-");
    }

    [Fact]
    public void Flow_passes_each_reference_as_a_native_argument_on_both_frames_and_thumbnails()
    {
        var cfg = new GflowSettings { ArgsTemplate = ["image"], ExtraArgs = [] };
        foreach (var thumbnail in new[] { false, true })
        {
            var refs = ReferenceStudySources.Images(StudiedChannel(), _dir, thumbnail);
            var args = GflowCliImageGenerator.BuildGenerationArgs("A scene", "output.png", cfg, references: refs);
            var refIndex = args.ToList().IndexOf("--ref");
            refIndex.Should().BeGreaterThan(0);
            args.Skip(refIndex + 1).Should().Equal(refs);
        }

        GflowCliImageGenerator.BuildGenerationArgs("A scene", "output.png", cfg).Should().NotContain("--ref");
    }

    [Fact]
    public void Flow_success_without_all_reference_uploads_is_not_accepted()
    {
        GflowCliImageGenerator.HasReferenceUploadEvidence("Done! output.png", 2).Should().BeFalse();
        GflowCliImageGenerator.HasReferenceUploadEvidence("Uploading reference\n   media_id=abc...\nDone!", 2).Should().BeFalse();
        GflowCliImageGenerator.HasReferenceUploadEvidence("   media_id=abc...\n   media_id=def...\nDone!", 2).Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"media_id\":\"reference-123\",\"path\":\"board.png\"}", "reference-123")]
    [InlineData("{\"media_id\":\"\"}", null)]
    [InlineData("{\"media_id\":null}", null)]
    [InlineData("{\"error\":\"upload failed\"}", null)]
    [InlineData("Done! output.png", null)]
    public void Flow_upload_requires_a_confirmed_media_id(string output, string? expected)
        => GflowCliImageGenerator.ParseUploadedReferenceId(output).Should().Be(expected);

    [Fact]
    public void Flow_generation_preserves_all_uploaded_reference_ids()
    {
        var cfg = new GflowSettings { ArgsTemplate = ["image"], ExtraArgs = [] };
        var ids = new[] { "characters-id", "scenes-id", "diagrams-id" };
        var args = GflowCliImageGenerator.BuildGenerationArgs("Scene", "out.png", cfg, references: ids);
        args.Skip(args.ToList().IndexOf("--ref") + 1).Should().Equal(ids);
    }

    [Fact]
    public void Named_topic_appropriate_template_overrides_rotation_and_typos_fail()
    {
        var channel = new Channel
        {
            ScriptTemplateName = "explainer",
            ScriptTemplates =
            [
                new ScriptTemplate { Name = "myths", Sections = ["debunk myths"] },
                new ScriptTemplate { Name = "explainer", Sections = ["explain the topic"], LastUsedUtc = DateTime.UtcNow },
            ],
        };
        ClaudeCliScriptGenerator.SelectStructure(channel).Should().Equal("explain the topic");
        channel.ScriptTemplateName = "missing";
        var act = () => ClaudeCliScriptGenerator.SelectStructure(channel);
        act.Should().Throw<InvalidOperationException>().WithMessage("*does not exist*");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
