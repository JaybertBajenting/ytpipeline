using FluentAssertions;
using YtPipeline.Stages.Images;

namespace YtPipeline.Tests;

/// <summary>
/// Codex answers a model it can't run with "not supported when using Codex with a ChatGPT account",
/// even when the real problem is an out-of-date CLI. The app has to say which one it is.
/// </summary>
public sealed class CodexRefusedModelTests
{
    // What CLI 0.155.1 printed for gpt-6.1-sol on 2026-09-30; 0.159.0 ran the same model fine.
    private const string OldCli =
        "warning: Model metadata for `gpt-6.1-sol` not found. Defaulting to fallback metadata; this can degrade performance and cause issues.\n" +
        "ERROR: {\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-6.1-sol' model is not supported when using Codex with a ChatGPT account.\"}}";

    [Fact]
    public void An_old_cli_is_told_to_update() =>
        CodexCliImageGenerator.ExplainRefusedModel(OldCli, "gpt-6.1-sol").Should()
            .Contain("older than that model").And.Contain("codex update");

    [Fact]
    public void A_model_the_account_cannot_use_says_so() =>
        CodexCliImageGenerator.ExplainRefusedModel(
                "ERROR: {\"message\":\"The 'gpt-9' model is not supported when using Codex with a ChatGPT account.\"}", "gpt-9")
            .Should().Contain("this account can't use gpt-9");

    [Fact]
    public void Other_failures_are_left_alone() =>
        CodexCliImageGenerator.ExplainRefusedModel("stream disconnected before completion", "gpt-6-astra").Should().BeNull();

    // Codex 0.159.2's stderr on 2026-09-30, when a thumbnail "finished" with no image six times.
    private const string SpentLimit =
        "2026-09-30T06:18:16.992326Z ERROR codex_core::tools::router: error=image generation failed: http 429 Too Many Requests: " +
        "Some(\"{\\\"error\\\":{\\\"type\\\":\\\"usage_limit_reached\\\",\\\"message\\\":\\\"The usage limit has been reached\\\"," +
        "\\\"plan_type\\\":\\\"plus\\\",\\\"resets_at\\\":1790816429,\\\"eligible_promo\\\":null,\\\"limit_window_minutes\\\":null,\\\"resets_in_seconds\\\":67332}}\")";

    [Fact]
    public void A_spent_image_limit_is_recognised_with_its_reset_time()
    {
        CodexCliImageGenerator.IsOutOfImages(SpentLimit, out var resets).Should().BeTrue();
        resets.Should().Be(new DateTime(2026, 10, 1, 1, 0, 29, DateTimeKind.Utc));
        CodexCliImageGenerator.OutOfImagesMessage(resets).Should()
            .Contain("used up its image generation limit")
            .And.Contain(resets!.Value.ToLocalTime().ToString("h:mm tt"));
    }

    [Fact]
    public void An_ordinary_run_is_not_mistaken_for_a_spent_limit() =>
        CodexCliImageGenerator.IsOutOfImages("Saved thumbnail.png", out _).Should().BeFalse();
}
