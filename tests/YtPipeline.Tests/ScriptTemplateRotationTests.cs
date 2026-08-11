using FluentAssertions;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Script;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// Rotation exists so consecutive uploads are not written to an identical skeleton. The rules that
/// matter: never repeat while an unused template exists, and always fall back to the single
/// structure rather than leaving a script unguided.
/// </summary>
public sealed class ScriptTemplateRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yttpl_" + Guid.NewGuid().ToString("N"));

    public ScriptTemplateRotationTests() => Directory.CreateDirectory(_dir);

    private static Channel WithTemplates(params string[] names)
    {
        var c = new Channel { ScriptStructure = { "FALLBACK: one section" } };
        foreach (var n in names)
            c.ScriptTemplates.Add(new ScriptTemplate { Name = n, Sections = { $"SECTION for {n}" } });
        return c;
    }

    [Fact]
    public void With_no_templates_it_uses_the_single_structure()
    {
        var c = new Channel { ScriptStructure = { "FALLBACK: one section" } };

        ClaudeCliScriptGenerator.PickTemplate(c).Should().BeNull();
        ClaudeCliScriptGenerator.SelectStructure(c).Should().ContainSingle()
            .Which.Should().Be("FALLBACK: one section");
    }

    [Fact]
    public void An_unused_template_is_always_preferred_over_a_used_one()
    {
        var c = WithTemplates("a", "b", "c");
        c.ScriptTemplates[0].LastUsedUtc = DateTime.UtcNow;   // 'a' already used

        ClaudeCliScriptGenerator.PickTemplate(c)!.Name.Should().BeOneOf("b", "c");
    }

    [Fact]
    public void It_picks_the_stalest_template_once_all_have_been_used()
    {
        var c = WithTemplates("a", "b", "c");
        c.ScriptTemplates[0].LastUsedUtc = DateTime.UtcNow.AddDays(-1);
        c.ScriptTemplates[1].LastUsedUtc = DateTime.UtcNow.AddDays(-5);   // stalest
        c.ScriptTemplates[2].LastUsedUtc = DateTime.UtcNow.AddHours(-2);

        ClaudeCliScriptGenerator.PickTemplate(c)!.Name.Should().Be("b");
    }

    [Fact]
    public void Stamping_use_moves_the_rotation_on()
    {
        var c = WithTemplates("a", "b");

        var first = ClaudeCliScriptGenerator.PickTemplate(c)!;
        first.LastUsedUtc = DateTime.UtcNow;

        var second = ClaudeCliScriptGenerator.PickTemplate(c)!;
        second.Name.Should().NotBe(first.Name, "back-to-back videos must not share a skeleton");
    }

    [Fact]
    public void Selected_structure_comes_from_the_chosen_template_not_the_fallback()
    {
        var c = WithTemplates("a");
        ClaudeCliScriptGenerator.SelectStructure(c).Should().ContainSingle()
            .Which.Should().Be("SECTION for a");
    }

    [Fact]
    public void An_empty_template_is_dropped_on_load_so_rotation_cannot_land_on_it()
    {
        // An empty skeleton would produce an unguided script — worse than the fallback.
        File.WriteAllText(Path.Combine(_dir, "ch.json"), """
        {
          "name": "Ch",
          "scriptStructure": [ "FALLBACK" ],
          "scriptTemplates": [
            { "name": "good", "sections": [ "A SECTION" ] },
            { "name": "empty", "sections": [] }
          ]
        }
        """);

        var loader = new ChannelLoader(_dir);
        var saved = loader.Save("ch", loader.Load("ch"));

        saved.ScriptTemplates.Should().ContainSingle().Which.Name.Should().Be("good");
    }

    [Fact]
    public void Templates_and_their_usage_survive_a_save_and_reload()
    {
        File.WriteAllText(Path.Combine(_dir, "ch2.json"), """
        { "name": "Ch2", "scriptTemplates": [ { "name": "t1", "sections": [ "S" ] } ] }
        """);

        var loader = new ChannelLoader(_dir);
        var c = loader.Load("ch2");
        var when = new DateTime(2026, 8, 11, 5, 0, 0, DateTimeKind.Utc);
        c.ScriptTemplates[0].LastUsedUtc = when;
        loader.Save("ch2", c);

        var reloaded = loader.Load("ch2");
        reloaded.ScriptTemplates.Should().ContainSingle();
        reloaded.ScriptTemplates[0].LastUsedUtc.Should().Be(when);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
