using FluentAssertions;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Images;
using Xunit;

namespace YtPipeline.Tests;

/// <summary>
/// <c>flow status</c> exits 0 even when it is reporting that generation cannot work, so health has
/// to be read out of its output rather than its exit code. Trusting the exit code showed
/// "Reachable" on the settings page throughout a 260-beat run in which all 260 beats failed.
/// </summary>
public sealed class GflowStatusTests
{
    /// <summary>Captured verbatim from the machine while the run was failing.</summary>
    private const string Disconnected = """
        Backend: up (http://127.0.0.1:8001)
          status:              unauthorized_or_disconnected
          extension_connected: False
          has_flow_key:        False
        """;

    private const string Connected = """
        Backend: up (http://127.0.0.1:8001)
          status:              ready
          extension_connected: True
          has_flow_key:        True
        """;

    [Fact]
    public void The_real_disconnected_output_is_read_as_unable_to_generate()
    {
        var s = GflowStatus.Parse(Disconnected);

        s.BackendUp.Should().BeTrue("the backend really was up — only the extension was missing");
        s.ExtensionConnected.Should().BeFalse();
        s.State.Should().Be("unauthorized_or_disconnected");
        s.CanGenerate.Should().BeFalse();
    }

    [Fact]
    public void A_disconnected_extension_is_told_which_url_to_open()
    {
        // The whole failure was a tab on flow.google.com instead of labs.google/fx/tools/flow, so
        // the remedy is worthless unless it names the URL.
        GflowStatus.Parse(Disconnected).Remedy
            .Should().Contain("https://labs.google/fx/tools/flow");
    }

    [Fact]
    public void A_connected_extension_can_generate_and_needs_no_remedy()
    {
        var s = GflowStatus.Parse(Connected);

        s.CanGenerate.Should().BeTrue();
        s.Remedy.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("command not found")]
    [InlineData("Backend: down")]
    public void Anything_unreadable_fails_closed(string? output)
    {
        // A status we could not read is not evidence that generation will work.
        GflowStatus.Parse(output).CanGenerate.Should().BeFalse();
    }

    [Theory]
    [InlineData("CAPTCHA_FAILED: Cannot access contents of url \"https://flow.google.com/\". Extension manifest must request permission to access this host.")]
    [InlineData("CAPTCHA_FAILED: NO_FLOW_TAB")]
    [InlineData("CAPTCHA_FAILED: The page keeping the extension port is moved into back/forward cache, so the message channel is closed.")]
    [InlineData("CAPTCHA_FAILED: Cannot access contents of url \"https://accounts.google.com/v3/signin/accountchooser?client_id=…\"")]
    public void Real_browser_failures_are_recognised(string stderr)
    {
        // All four of these appeared in one afternoon's log; they are one problem, not four.
        GflowCliImageGenerator.LooksLikeBrowserProblem(stderr).Should().BeTrue();
    }

    [Fact]
    public void A_browser_problem_is_not_mistaken_for_a_spent_quota()
    {
        // These must stay distinct: quota benches the provider and clears on a timer, a browser
        // problem does neither and needs the user to act. Conflating them sent one investigation
        // down the wrong path already.
        const string captcha = "CAPTCHA_FAILED: Cannot access contents of url \"https://flow.google.com/\".";

        GflowCliImageGenerator.LooksLikeBrowserProblem(captcha).Should().BeTrue();
        ProviderHealthStore.LooksLikeQuotaExhaustion(captcha).Should().BeFalse();
    }

    [Fact]
    public void A_spent_quota_is_not_mistaken_for_a_browser_problem()
    {
        const string quota = "PUBLIC_ERROR_PER_MODEL_DAILY_QUOTA_REACHED";

        ProviderHealthStore.LooksLikeQuotaExhaustion(quota).Should().BeTrue();
        GflowCliImageGenerator.LooksLikeBrowserProblem(quota).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_stderr_is_not_a_browser_problem(string? text)
        => GflowCliImageGenerator.LooksLikeBrowserProblem(text).Should().BeFalse();
}
