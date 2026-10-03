namespace YtPipeline.Stages.Images;

/// <summary>
/// What <c>flow status</c> reports about whether gflow can actually generate right now.
/// </summary>
/// <remarks>
/// Parsed rather than inferred from the exit code, because <c>flow status</c> **exits 0 even when
/// the extension is disconnected** — it succeeded in reporting bad news. Treating that exit code as
/// health made the settings page say "Reachable" at the exact moment every generation was failing,
/// and turned a one-line problem into a log dive.
/// </remarks>
public sealed record GflowStatus(bool BackendUp, bool ExtensionConnected, string State)
{
    /// <summary>
    /// The only page the Flow extension attaches to.
    /// <para>
    /// Its manifest lists <c>https://labs.google/*</c> and its content script matches
    /// <c>labs.google/fx/tools/flow*</c>; <c>flow.google.com</c> appears nowhere in the extension
    /// at all. A tab on that other host produces "Cannot access contents of url … Extension
    /// manifest must request permission to access this host" on every single call, which is a
    /// confusing way to be told the URL is wrong.
    /// </para>
    /// </summary>
    public const string FlowTabUrl = "https://labs.google/fx/tools/flow";

    /// <summary>True when gflow is in a state that can actually produce an image.</summary>
    public bool CanGenerate => BackendUp && ExtensionConnected;

    /// <summary>What to tell the user to do, or null when nothing is wrong.</summary>
    public string? Remedy =>
        CanGenerate ? null
        : !BackendUp ? "Flow's local backend isn't running. Run `flow status` once to start it."
        : $"Flow's browser extension isn't connected. Open {FlowTabUrl} in Chrome, sign in, and "
          + "leave that tab open — the extension only attaches to that exact page, not to "
          + "flow.google.com.";

    /// <summary>
    /// Reads the probe's own output. Unknown lines are ignored, and anything unparseable comes back
    /// as "not connected" — a status we could not read is not evidence that generation will work.
    /// </summary>
    public static GflowStatus Parse(string? stdout)
    {
        var text = stdout ?? "";
        return new GflowStatus(
            BackendUp: Field(text, "Backend:")?.StartsWith("up", StringComparison.OrdinalIgnoreCase) ?? false,
            ExtensionConnected: string.Equals(Field(text, "extension_connected:"), "true",
                                              StringComparison.OrdinalIgnoreCase),
            State: Field(text, "status:") ?? "unknown");
    }

    /// <summary>The remainder of the first line carrying <paramref name="label"/>, trimmed.</summary>
    private static string? Field(string text, string label)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var value = line[(at + label.Length)..].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }
}
