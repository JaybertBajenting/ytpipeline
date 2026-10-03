using FluentAssertions;
using YtPipeline.Core.Services;
using Xunit;

namespace YtPipeline.Tests;

public sealed class ProviderHealthStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytph_" + Guid.NewGuid().ToString("N"));
    private const int Base = 60;

    public ProviderHealthStoreTests() => Directory.CreateDirectory(_dir);

    private ProviderHealthStore New() => new(_dir);

    [Theory]
    [InlineData("PUBLIC_ERROR_PER_MODEL_DAILY_QUOTA_REACHED", true)]
    [InlineData("Resource has been exhausted (e.g. check quota).", true)]
    [InlineData("RESOURCE_EXHAUSTED", true)]
    [InlineData("CAPTCHA_FAILED: Cannot access contents of the page.", false)]
    [InlineData("401 OAuth access token has expired.", false)]
    [InlineData("", false)]
    public void Only_quota_errors_count_as_exhaustion(string text, bool expected)
    {
        // A CAPTCHA or auth failure must NOT bench a provider — benching would route around a
        // problem the user can actually fix, and hide it.
        ProviderHealthStore.LooksLikeQuotaExhaustion(text).Should().Be(expected);
    }

    [Fact]
    public void A_stated_reset_benches_until_exactly_then_and_success_clears_it()
    {
        // Codex's 429 names its reset (2026-09-30: 18 hours out). Retrying after the 60-minute
        // cooldown would just hit the same wall, so the stated time wins.
        var store = New();
        var resets = DateTime.UtcNow.AddHours(18);
        store.MarkExhausted("codex", "usage_limit_reached", Base, resets).Should().BeTrue();
        store.BenchedUntil("codex", Base).Should().Be(resets);
        New().BenchedUntil("codex", Base).Should().Be(resets, "it is persisted");

        store.MarkHealthy("codex");
        store.BenchedUntil("codex", Base).Should().BeNull();
        store.Snapshot("codex").ResetsUtc.Should().BeNull();
    }

    [Fact]
    public void A_stated_reset_in_the_past_does_not_bench()
    {
        var store = New();
        store.MarkExhausted("codex", "usage_limit_reached", Base, DateTime.UtcNow.AddMinutes(-1));
        store.BenchedUntil("codex", Base).Should().BeNull();
    }

    [Fact]
    public void First_report_benches_the_provider()
    {
        var store = New();
        store.MarkExhausted("gflow", "QUOTA_REACHED", Base).Should().BeTrue("it starts an episode");
        store.BenchedUntil("gflow", Base).Should().NotBeNull();
        store.Snapshot("gflow").ConsecutiveProbeFailures.Should().Be(0);
    }

    [Fact]
    public void Repeat_reports_during_one_run_do_not_inflate_the_backoff()
    {
        // Concurrent lanes each report the same wall. Counting them separately drove the counter
        // to its cap inside one run and kept sliding the cooldown so it never expired.
        var store = New();
        store.MarkExhausted("gflow", "QUOTA_REACHED", Base).Should().BeTrue();

        var first = store.BenchedUntil("gflow", Base);

        for (int i = 0; i < 12; i++)
            store.MarkExhausted("gflow", "QUOTA_REACHED", Base)
                 .Should().BeFalse("later reports fold into the same episode");

        store.Snapshot("gflow").ConsecutiveProbeFailures.Should().Be(0);
        store.BenchedUntil("gflow", Base).Should().Be(first, "the cooldown must not slide forward");
    }

    [Fact]
    public void Success_clears_the_benching_immediately()
    {
        var store = New();
        store.MarkExhausted("gflow", "QUOTA_REACHED", Base);
        store.MarkHealthy("gflow");

        store.BenchedUntil("gflow", Base).Should().BeNull();
        store.Snapshot("gflow").ConsecutiveProbeFailures.Should().Be(0);
    }

    [Fact]
    public void Cooldown_backs_off_per_failed_retry_and_is_capped()
    {
        ProviderHealthStore.Cooldown(60, 0).Should().Be(TimeSpan.FromMinutes(60));
        ProviderHealthStore.Cooldown(60, 1).Should().Be(TimeSpan.FromMinutes(120));
        ProviderHealthStore.Cooldown(60, 2).Should().Be(TimeSpan.FromMinutes(240));
        ProviderHealthStore.Cooldown(60, 9).Should().Be(TimeSpan.FromHours(8), "capped");
        ProviderHealthStore.Cooldown(1, 0).Should().Be(TimeSpan.FromMinutes(5), "floor");
    }

    [Fact]
    public void State_survives_a_restart()
    {
        New().MarkExhausted("gflow", "QUOTA_REACHED", Base);
        New().BenchedUntil("gflow", Base).Should().NotBeNull("it is persisted, not in-memory");
    }

    [Fact]
    public void Reset_forgets_the_provider()
    {
        var store = New();
        store.MarkExhausted("gflow", "QUOTA_REACHED", Base);
        store.Reset("gflow");
        store.BenchedUntil("gflow", Base).Should().BeNull();
    }

    [Fact]
    public void An_untouched_provider_is_never_benched()
    {
        New().BenchedUntil("codex", Base).Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
