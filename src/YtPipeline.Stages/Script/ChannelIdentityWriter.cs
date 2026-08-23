using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YtPipeline.Core.Models;
using YtPipeline.Core.Services;
using YtPipeline.Stages.Process;

namespace YtPipeline.Stages.Script;

/// <summary>
/// Writes a channel's public YouTube identity — handle, byline, About text and keywords — from the
/// persona and tone already configured for it. The same Claude CLI that writes the scripts, so the
/// About page reads like the channel rather than like generic marketing copy.
/// </summary>
public sealed partial class ChannelIdentityWriter(
    ProcessRunner process,
    ClaudeOptions options,
    ILogger<ChannelIdentityWriter> logger)
{
    /// <summary>YouTube's own cap on a channel description.</summary>
    public const int BioMaxChars = 1000;

    /// <summary>Suggests an identity. Never saves — the caller reviews it before it is written.</summary>
    public async Task<ChannelIdentity> WriteAsync(Channel channel, CancellationToken ct)
    {
        logger.LogInformation("Writing channel identity for '{Channel}'…", channel.Name);

        var args = new List<string>(options.Args) { BuildPrompt(channel) };
        var result = await process.RunOrThrowAsync(options.Executable, args, ct: ct);

        var json = ClaudeCliScriptGenerator.ExtractFirstJsonObject(result.StdOut)
            ?? throw new InvalidOperationException(
                "Claude returned no JSON for the channel identity. Check the claude CLI is working.");

        var written = Json.Deserialize<ChannelIdentity>(json)
            ?? throw new InvalidOperationException("Could not read the channel identity Claude returned.");

        var clean = Sanitize(written);
        logger.LogInformation(
            "Identity written: handle '{Handle}', {Bio} char About, {Keywords} keyword(s).",
            clean.Handle, clean.Bio.Length, clean.Keywords.Count);
        return clean;
    }

    /// <summary>
    /// Trims the model's answer to what YouTube will actually accept. A rejected paste is worse
    /// than a slightly short one, so limits are enforced here rather than merely requested.
    /// </summary>
    private static ChannelIdentity Sanitize(ChannelIdentity id)
    {
        var handle = HandleChars().Replace((id.Handle ?? "").Trim().TrimStart('@').ToLowerInvariant(), "");
        if (handle.Length > 30) handle = handle[..30];

        var bio = (id.Bio ?? "").Trim();
        if (bio.Length > BioMaxChars) bio = bio[..BioMaxChars].TrimEnd();

        return new ChannelIdentity
        {
            // Under 3 characters is not a legal handle, so an empty box beats an invalid suggestion.
            Handle = handle.Length >= 3 ? handle : "",
            Byline = (id.Byline ?? "").Trim(),
            Bio = bio,
            Keywords = (id.Keywords ?? [])
                .Select(k => (k ?? "").Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList(),
            // Art direction is the user's own note — never invented, never overwritten.
            AvatarPrompt = "",
            BannerPrompt = "",
            Links = [],
        };
    }

    private static string BuildPrompt(Channel channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Write the public YouTube channel identity for the channel described below.");
        sb.AppendLine("This is the channel's own About page and handle — NOT a description for a single video.");
        sb.AppendLine();

        sb.AppendLine("=== THE CHANNEL ===");
        sb.AppendLine($"Name: {channel.Name}");
        if (!string.IsNullOrWhiteSpace(channel.Tagline)) sb.AppendLine($"Tagline: {channel.Tagline}");
        if (!string.IsNullOrWhiteSpace(channel.Persona)) sb.AppendLine($"Narrator persona: {channel.Persona}");
        if (!string.IsNullOrWhiteSpace(channel.Audience)) sb.AppendLine($"Audience: {channel.Audience}");
        if (!string.IsNullOrWhiteSpace(channel.Tone)) sb.AppendLine($"Tone: {channel.Tone}");
        if (!string.IsNullOrWhiteSpace(channel.ScriptVoiceRules))
            sb.AppendLine($"Sentence-level voice rules: {channel.ScriptVoiceRules}");
        sb.AppendLine();

        sb.AppendLine("Requirements:");
        sb.AppendLine("- bio: the About-page description, written IN THE CHANNEL'S OWN VOICE using the tone and");
        sb.AppendLine($"  voice rules above. Two or three short paragraphs, {BioMaxChars} characters MAXIMUM.");
        sb.AppendLine("  Say what the viewer gets and why it is worth their time. No hashtags. No emoji.");
        sb.AppendLine("  Never say 'welcome to my channel', 'don't forget to subscribe', or 'hit that bell'.");
        sb.AppendLine("- byline: ONE line, under 100 characters, that could sit beneath the channel name.");
        sb.AppendLine("- handle: a YouTube handle WITHOUT the @, 3-30 characters, lowercase letters, digits,");
        sb.AppendLine("  dots, dashes or underscores only. Base it on the channel name. No spaces.");
        sb.AppendLine("- keywords: 8-12 short search terms someone would actually type to find this channel.");
        sb.AppendLine();

        sb.AppendLine("Return ONLY a single JSON object (no markdown, no prose, no code fences):");
        sb.AppendLine("""
        {
          "handle": "string, no @",
          "byline": "string, under 100 chars",
          "bio": "string, <= 1000 chars",
          "keywords": ["string", "..."]
        }
        """);
        return sb.ToString();
    }

    [GeneratedRegex("[^a-z0-9._-]")]
    private static partial Regex HandleChars();
}
