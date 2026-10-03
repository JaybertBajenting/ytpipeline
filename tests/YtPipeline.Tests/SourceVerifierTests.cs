using FluentAssertions;
using YtPipeline.Stages.Script;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// The first sourced description this channel produced had two wrong citations out of eight — a
/// paper credited to a co-author instead of its first author, and a year that did not match the
/// record. Both read exactly like the six correct ones. These cover the parsing and correction
/// either side of the network lookup, using the real lines that failed.
/// </summary>
public sealed class SourceVerifierTests
{
    private const string Description = """
        When Did Humans Start Cooking Their Food?

        Some teaser text about fire and food.

        All research and sources are linked below.

        ————————————————————
        RESEARCH & SOURCES:
        > Ran Barkai and Avi Gopher, Qesem Cave — "Evidence for the repeated use of a central hearth at Middle Pleistocene (300 ky ago) Qesem Cave, Israel" (2014, Journal of Archaeological Science) — a single central hearth used over and over.
        > Richard Wrangham, cooking and human evolution — "Catching Fire: How Cooking Made Us Human" (2009, Basic Books) — argues cooking is pre-digestion outside the body.
        ————————————————————
        PEOPLE & SITES MENTIONED IN THIS VIDEO:
        > Qesem Cave (Israel) — "not a citation" and should never be parsed as one.
        """;

    [Fact]
    public void Parses_only_the_sources_block()
    {
        var entries = SourceVerifier.Parse(Description);

        entries.Should().HaveCount(2);
        entries[0].Title.Should().StartWith("Evidence for the repeated use of a central hearth");
        entries[1].Title.Should().Be("Catching Fire: How Cooking Made Us Human");
    }

    [Fact]
    public void Reads_the_year_and_the_person_credited()
    {
        var entry = SourceVerifier.Parse(Description)[0];

        entry.Year.Should().Be("2014");
        entry.NamedPerson.Should().Be("Ran Barkai and Avi Gopher");
    }

    [Fact]
    public void Ignores_quoted_text_outside_the_sources_block() =>
        SourceVerifier.Parse(Description).Should().NotContain(e => e.Title.Contains("not a citation"));

    [Fact]
    public void Matches_a_record_whose_title_is_the_same_work() =>
        SourceVerifier.SameWork(
            "Evidence for the repeated use of a central hearth at Middle Pleistocene (300 ky ago) Qesem Cave, Israel",
            "Evidence for the repeated use of a central hearth at Middle Pleistocene (300 ky ago) Qesem Cave, Israel")
        .Should().BeTrue();

    [Fact]
    public void Rejects_a_different_paper_from_the_same_site() =>
        SourceVerifier.SameWork(
            "Evidence for the repeated use of a central hearth at Middle Pleistocene Qesem Cave, Israel",
            "Cutting for a purpose: butchery and blade production at Qesem Cave")
        .Should().BeFalse();

    [Fact]
    public void Corrects_the_year_and_venue_and_adds_the_real_doi()
    {
        var entries = SourceVerifier.Parse(Description);
        var checks = new[]
        {
            new SourceCheck(entries[0], true,
                CanonicalTitle: entries[0].Title, FirstAuthor: "Shahack-Gross", Year: 2014,
                Venue: "Journal of Archaeological Science", Doi: "10.1016/j.jas.2013.11.015"),
            new SourceCheck(entries[1], true,
                CanonicalTitle: entries[1].Title, FirstAuthor: "Wrangham", Year: 2009,
                Venue: "Basic Books", Note: "book"),
        };

        var corrected = SourceVerifier.ApplyCorrections(Description, checks);

        // The attribution half is deliberately left as written — see Correct().
        corrected.Should().Contain("Ran Barkai and Avi Gopher");
        corrected.Should().Contain("https://doi.org/10.1016/j.jas.2013.11.015");
        // The book verified too, so it stays — it simply has no DOI to add.
        corrected.Should().Contain("Catching Fire");
    }

    [Fact]
    public void Drops_a_citation_nothing_could_find()
    {
        var entries = SourceVerifier.Parse(Description);
        var checks = new[]
        {
            new SourceCheck(entries[0], false, Note: "not found"),
            new SourceCheck(entries[1], true,
                CanonicalTitle: entries[1].Title, FirstAuthor: "Wrangham", Year: 2009, Venue: "Basic Books"),
        };

        var corrected = SourceVerifier.ApplyCorrections(Description, checks);

        corrected.Should().NotContain("Qesem Cave, Israel\" (2014");
        corrected.Should().Contain("Catching Fire");
        // Everything that isn't a citation survives untouched.
        corrected.Should().Contain("RESEARCH & SOURCES:");
        corrected.Should().Contain("PEOPLE & SITES MENTIONED IN THIS VIDEO:");
    }

    [Fact]
    public void Never_rewrites_the_quoted_title_when_fixing_the_attribution()
    {
        // The attribution here is a SITE, not a person. Replacing it blindly rewrote the paper's
        // title into "Fire at Goren-Inbar et al., Israel" — a paper that does not exist.
        const string line =
            "> Gesher Benot Ya'aqov, Israel — \"Evidence of Hominin Control of Fire at Gesher Benot Ya'aqov, Israel\" (2004, Science) — burnt seeds and flint.";
        var description = $"RESEARCH & SOURCES:\n{line}\n";
        var entry = SourceVerifier.Parse(description).Single();

        var corrected = SourceVerifier.ApplyCorrections(description, [
            new SourceCheck(entry, true, CanonicalTitle: entry.Title, FirstAuthor: "Goren-Inbar",
                Year: 2004, Venue: "Science", Doi: "10.1126/science.1095443"),
        ]);

        corrected.Should().Contain("\"Evidence of Hominin Control of Fire at Gesher Benot Ya'aqov, Israel\"");
        corrected.Should().NotContain("Fire at Goren-Inbar");
        corrected.Should().Contain("> Gesher Benot Ya'aqov, Israel —");
        corrected.Should().Contain("https://doi.org/10.1126/science.1095443");
    }

    [Fact]
    public void A_citation_that_could_not_be_checked_is_kept_untouched()
    {
        // Crossref answered 429 once and a real paper was deleted as fabricated. An unanswered
        // question must never read as a denial.
        var entries = SourceVerifier.Parse(Description);
        var corrected = SourceVerifier.ApplyCorrections(Description, [
            new SourceCheck(entries[0], false, Note: "lookup unavailable", Checked: false),
        ]);

        corrected.Should().Contain("Evidence for the repeated use of a central hearth");
        corrected.Should().Contain("Ran Barkai and Avi Gopher");
    }

    [Fact]
    public void Only_a_denied_citation_counts_as_fabricated()
    {
        var entry = SourceVerifier.Parse(Description)[0];

        new SourceCheck(entry, false, Note: "not found").Fabricated.Should().BeTrue();
        new SourceCheck(entry, false, Note: "lookup unavailable", Checked: false).Fabricated.Should().BeFalse();
        new SourceCheck(entry, true, FirstAuthor: "Shahack-Gross").Fabricated.Should().BeFalse();
    }

    [Fact]
    public void An_unchanged_correct_entry_keeps_its_wording()
    {
        var entries = SourceVerifier.Parse(Description);
        var checks = new[]
        {
            new SourceCheck(entries[1], true,
                CanonicalTitle: entries[1].Title, FirstAuthor: "Wrangham", Year: 2009, Venue: "Basic Books"),
        };

        SourceVerifier.ApplyCorrections(Description, checks)
            .Should().Contain("Richard Wrangham, cooking and human evolution");
    }
}
