# YouTube 3-Channel Automation Pipeline — Project Spec

> **Stack:** .NET 9 console application + worker services
> **Author:** Jaybert
> **Status:** Specification (pre-build)
> **Goal:** Produce and publish one video per channel every 3 days across 3 channels, with a 2 hr/day human review window.

---

## 1. Overview

A pipeline that takes a topic + channel identity and produces a finished, uploaded YouTube video through five automated stages. Each stage is independently runnable for debugging; an orchestrator chains them. Human review happens at defined checkpoints, not by babysitting the machine.

The architecture is deliberately **CLI/process-driven first** (no n8n yet). Orchestration is plain C#. n8n can wrap this later by calling the same console commands.

### The five stages

| # | Stage | Tool | Output |
|---|---|---|---|
| 1 | Script generation | Claude Code CLI (subprocess) | `script.json` |
| 2 | Image generation | Codex CLI (subprocess) | `images/*.png` |
| 3 | Voiceover | ElevenLabs API (HTTP) | `audio/voice.mp3` |
| 4 | Video assembly | FFmpeg (subprocess) | `video/final.mp4` |
| 5 | Upload | YouTube Data API v3 | published video |

---

## 2. Why .NET console + subprocess

Two of the five stages (Claude Code, Codex) are CLI tools with no .NET SDK. The pipeline shells out to them via `System.Diagnostics.Process`. The remaining stages (ElevenLabs, YouTube) are HTTP APIs with clean .NET clients. FFmpeg is also a subprocess. So the app is fundamentally an **orchestrator of processes + HTTP calls**, which a .NET console host does cleanly.

A console app (not a web API) because there is no inbound request — it is invoked on a schedule or manually.

---

## 3. Solution structure

```
YtPipeline.sln
│
├── src/
│   ├── YtPipeline.Core/             # domain: Run, Manifest, Channel, ScriptDoc
│   │   ├── Models/
│   │   │   ├── Channel.cs
│   │   │   ├── ScriptDocument.cs
│   │   │   ├── ScriptBeat.cs
│   │   │   ├── Run.cs
│   │   │   └── Manifest.cs
│   │   ├── Abstractions/            # interfaces for each stage
│   │   │   ├── IScriptGenerator.cs
│   │   │   ├── IImageGenerator.cs
│   │   │   ├── IVoiceGenerator.cs
│   │   │   ├── IVideoAssembler.cs
│   │   │   └── IVideoUploader.cs
│   │   └── Services/
│   │       ├── RunManager.cs        # run ids, manifest read/write
│   │       └── ChannelLoader.cs
│   │
│   ├── YtPipeline.Stages/           # concrete implementations
│   │   ├── Script/ClaudeCliScriptGenerator.cs
│   │   ├── Images/CodexCliImageGenerator.cs
│   │   ├── Voice/ElevenLabsVoiceGenerator.cs
│   │   ├── Video/FfmpegVideoAssembler.cs
│   │   ├── Upload/YouTubeUploader.cs
│   │   └── Process/ProcessRunner.cs # shared subprocess helper
│   │
│   └── YtPipeline.Cli/              # entry point + commands
│       ├── Program.cs
│       ├── Commands/
│       │   ├── RunPipelineCommand.cs
│       │   ├── GenerateScriptCommand.cs
│       │   ├── GenerateImagesCommand.cs
│       │   ├── GenerateVoiceCommand.cs
│       │   ├── AssembleVideoCommand.cs
│       │   └── UploadCommand.cs
│       ├── appsettings.json
│       └── .env                     # secrets (gitignored)
│
├── channels/                        # per-channel JSON context
│   ├── nullpointer.json
│   ├── cavefinance.json
│   └── doodledamage.json
│
├── assets/                          # working files per run
│   ├── images/<run_id>/
│   ├── audio/<run_id>/
│   └── video/<run_id>/
│
├── output/                          # run manifests
│   └── <run_id>/manifest.json
│
└── tests/
    └── YtPipeline.Tests/
```

---

## 4. The "Run" concept

Each video is a **run**, keyed by `run_id = {channel}_{yyyyMMdd_HHmmss}`.

Everything for that run is tracked in `output/<run_id>/manifest.json`. Any stage reads the manifest, does its work, writes its outputs into the run's asset folders, and updates the manifest. This makes every stage **resumable and idempotent** — re-running stage 3 after a failed stage 4 costs nothing extra.

### Manifest shape

```json
{
  "runId": "nullpointer_20260605_153000",
  "channel": "nullpointer",
  "topic": "How DNS actually works",
  "createdUtc": "2026-06-05T15:30:00Z",
  "stages": {
    "script":   { "done": true, "path": "output/.../script.json", "completedUtc": "..." },
    "images":   { "done": true, "count": 5, "dir": "assets/images/..." },
    "voice":    { "done": true, "path": "assets/audio/.../voice.mp3", "seconds": 287 },
    "video":    { "done": true, "path": "assets/video/.../final.mp4" },
    "upload":   { "done": false }
  },
  "review": {
    "scriptApproved": false,
    "videoApproved": false
  }
}
```

---

## 5. Channel context model

A channel is a JSON file describing identity, structure, visual style, and YouTube defaults. It is the stable input that makes generation consistent across videos.

```jsonc
{
  "name": "NullPointer",
  "tagline": "Tech explainers with Ptr the wizard",
  "persona": "Ptr, a wry wizard explaining tech with magic metaphors...",
  "audience": "Junior to mid developers",
  "tone": "Clear, warm, a little playful",
  "videoLengthTargetMinutes": 5,
  "scriptStructure": [
    "Cold open hook",
    "Plain-language answer",
    "Mechanism in 3-4 beats with a running metaphor",
    "One misconception corrected",
    "Recap + outro"
  ],
  "visualStyle": "MS Paint stick-figure aesthetic, 16:9, flat colors...",
  "voiceNotes": "Measured pace, friendly",
  "youtube": {
    "categoryId": "27",
    "defaultTags": ["programming", "tech explained"],
    "titlePattern": "{topic} — explained simply",
    "descriptionFooter": "\n\n~ Ptr\nNew explainer every 3 days."
  }
}
```

---

## 6. Stage contracts

Each stage implements a small interface from `YtPipeline.Core.Abstractions`. This keeps the orchestrator dumb and the stages swappable (e.g. swap ElevenLabs for another TTS without touching orchestration).

```csharp
public interface IScriptGenerator
{
    Task<ScriptDocument> GenerateAsync(Channel channel, string topic, CancellationToken ct);
}

public interface IImageGenerator
{
    // one image per script beat
    Task<IReadOnlyList<string>> GenerateAsync(Run run, ScriptDocument script, CancellationToken ct);
}

public interface IVoiceGenerator
{
    Task<string> GenerateAsync(Run run, ScriptDocument script, Channel channel, CancellationToken ct);
}

public interface IVideoAssembler
{
    Task<string> AssembleAsync(Run run, ScriptDocument script,
                               IReadOnlyList<string> images, string audioPath, CancellationToken ct);
}

public interface IVideoUploader
{
    Task<string> UploadAsync(Run run, Channel channel, ScriptDocument script,
                             string videoPath, string privacy, CancellationToken ct);
}
```

### ScriptDocument

The shared currency between stages. Stage 1 produces it; stages 2–5 consume it.

```csharp
public sealed class ScriptDocument
{
    public string Title { get; set; }
    public string Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<ScriptBeat> Beats { get; set; } = new();   // drives images + timing
    public string FullNarration { get; set; }              // drives voiceover
}

public sealed class ScriptBeat
{
    public int Index { get; set; }
    public string Narration { get; set; }     // what's spoken over this beat
    public string ImagePrompt { get; set; }   // what Codex draws for this beat
}
```

---

## 7. Stage detail

### Stage 1 — Script generation (Claude Code CLI)
- Build a prompt from the channel context + topic.
- Invoke `claude` as a subprocess, instructing it to return **only JSON** matching `ScriptDocument`.
- Parse + validate (FluentValidation). Save `script.json`.
- **Review checkpoint:** `scriptApproved` gate before stage 2.

### Stage 2 — Image generation (Codex CLI)
- For each beat's `ImagePrompt`, prepend the channel `visualStyle`.
- Invoke `codex` with the image-gen skill, one image per beat, 16:9.
- Save to `assets/images/<run_id>/beat_{n}.png`.
- **Cost watch:** image-gen turns burn Plus limits 3–5× faster. Log count per run; warn if a run exceeds N images.

### Stage 3 — Voiceover (ElevenLabs API)
- POST `FullNarration` to the TTS endpoint with the channel's voice id.
- Save `voice.mp3`, capture duration (drives video timing).
- **Budget:** ~50 min/month across 3 channels at this cadence; well under the 100-min Creator cap.

### Stage 4 — Video assembly (FFmpeg)
- Map each image to its beat's spoken duration (from per-beat TTS or proportional split).
- Build an FFmpeg concat/filtergraph: image sequence + audio track → `final.mp4` (1080p, 16:9, H.264).
- Optional: simple crossfades, static intro/outro.
- **Review checkpoint:** `videoApproved` gate before stage 5.

### Stage 5 — Upload (YouTube Data API v3)
- OAuth desktop flow (one-time consent per channel; refresh token cached per channel).
- Resumable upload of `final.mp4` with title/description/tags from `ScriptDocument`.
- Default privacy `private` until proven; flip to `public`/`scheduled` later.

---

## 8. Orchestration & CLI

Single console app, subcommands (System.CommandLine):

```bash
# full run
ytpipe run --channel nullpointer --topic "How DNS actually works"

# single stage (resumes from manifest)
ytpipe script   --channel nullpointer --topic "..."
ytpipe images   --run latest
ytpipe voice    --run latest
ytpipe video    --run latest
ytpipe upload   --run latest --privacy private

# approve a checkpoint
ytpipe approve  --run latest --stage script
```

Orchestrator stops at unapproved checkpoints and prints what to review. This is where the 2 hr/day goes.

---

## 9. Dependencies (matched to your installed versions)

| Package | Version | Use |
|---|---|---|
| Microsoft.Extensions.Hosting | 9.0.x | generic host, DI, config |
| Microsoft.Extensions.Configuration | 9.0.9 | appsettings + env |
| DotNetEnv | 3.1.1 | load `.env` secrets |
| Newtonsoft.Json | 13.0.4 | manifest + script JSON |
| FluentValidation | 12.0.0 | validate generated ScriptDocument |
| Serilog.Extensions.Hosting | 8.0.0 | structured logging |
| Serilog.Sinks.Console | 6.0.0 | console logs |
| Serilog.Sinks.File | 7.0.0 | per-run log files |
| Google.Apis.YouTube.v3 | latest stable* | upload (stage 5) |
| Refit.HttpClientFactory | 8.0.0 | ElevenLabs HTTP client (stage 3) |
| System.CommandLine | latest stable* | CLI subcommands |
| xUnit | 2.9.3 | tests |
| Moq | 4.20.72 | mock stage interfaces |
| FluentAssertions | 7.2.0 | test assertions |

\* `Google.Apis.YouTube.v3` and `System.CommandLine` aren't in your deps reference yet — I'll pin them to latest stable at build time and you can paste your `.csproj` after to lock them in.

---

## 10. Configuration & secrets

`.env` (gitignored):
```
ELEVENLABS_API_KEY=
ELEVENLABS_VOICE_ID=
YOUTUBE_CLIENT_SECRETS=client_secret.json
```

CLI tool auth (`claude`, `codex`) is handled by their own login — no keys in this repo.

---

## 11. Build order (recommended)

1. **Core models + RunManager + ChannelLoader** — the skeleton everything hangs on.
2. **ProcessRunner** — shared subprocess helper (used by stages 1, 2, 4).
3. **Stage 1 (script)** end-to-end with a `--dry-run` that prints the prompt instead of calling `claude`.
4. **Stages 3 + 4** (voice + video) — these are the most mechanical and testable.
5. **Stage 2 (images)** — wire Codex once script shape is stable.
6. **Stage 5 (upload)** — last, with `private` default.
7. **Orchestrator + checkpoints.**
8. **n8n wrapper** (future) — calls `ytpipe` commands on a schedule.

---

## 12. Risks / open items

- **Codex CLI image flags** — exact subprocess invocation needs confirming against your installed Codex version.
- **Claude Code CLI JSON reliability** — may need a retry/repair pass if it wraps JSON in prose. Validation catches this.
- **Per-beat timing** — simplest v1 splits audio proportionally across beats; better v1.1 generates per-beat audio for exact sync.
- **YouTube quota** — Data API upload costs ~1600 quota units each; default 10k/day quota is fine for 1 video/channel/3 days but worth noting.
- **.NET 8 vs 9** — spec targets .NET 9 to match your installed stack; say the word if this project must be 8.
