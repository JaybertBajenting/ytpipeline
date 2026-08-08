# YtPipeline — Application Documentation

A .NET 9 automation pipeline that turns a **channel + topic** into a finished, ready-to-publish
YouTube video. It chains five stages — **script → voiceover → images → video → upload** — each
of which shells out to an AI/media tool (Claude, ElevenLabs, Codex, FFmpeg, YouTube). Every video
is a resumable **run** with a single manifest as its source of truth, driven either from a CLI or
a React web UI.

> This document describes the whole app: architecture, data model, each stage, the web/CLI
> front-ends, configuration, and how to build & run. For a quick-start, see [`README.md`](README.md).

---

## 1. High-level overview

```
            ┌─────────┐   ┌──────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐
 topic ───► │ 1 Script│─► │ 2 Voice  │─► │ 3 Images│─► │ 4 Video │─► │5 Upload │ ──► YouTube
            │ (Claude)│   │(ElevenL.)│   │ (Codex) │   │ (FFmpeg)│   │(YouTube)│
            └─────────┘   └──────────┘   └─────────┘   └─────────┘   └─────────┘
                 │             │             │             │             │
                 └─────────────┴──── manifest.json (state, per run) ─────┘
                       ▲ script checkpoint            ▲ video checkpoint
                       │ (human approval)             │ (human approval)
```

- **One run at a time per video**, keyed `{channel}_{yyyyMMdd_HHmmss}`.
- **Resumable & idempotent:** each stage reads the manifest, does its work, writes progress back.
- **Two human checkpoints:** the pipeline pauses after *script* and after *video* until approved.
- **Pluggable hosts:** the same pipeline core is driven by a CLI (`ytpipe`) and an ASP.NET Core
  Web API + React UI. Both wire up identically through one DI registration.

### Stage order note

The real execution order is **script → voice → images → video → upload**. Voice runs before
images because precise audio/video sync uses ElevenLabs' per-character timestamps (captured in
the voice stage) to time each image. (Some older prose says "script → images → voice"; the code
order above is authoritative.)

---

## 2. End-to-end flow (one run)

This traces a single video from a topic to a published upload, showing **what triggers each step,
what it writes, and where the pipeline pauses**. The CLI and the web UI drive the exact same
`Pipeline` methods — only the trigger differs (a command vs. a button / background job).

```
 ┌── trigger ──────────────────────────────────────────────────────────────────┐
 │  CLI:  ytpipe run --channel nullpointer --topic "How DNS actually works"      │
 │  Web:  "New video" → pick channel + topic → Generate  (or Auto-run toggle)    │
 └───────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
 [0] RunManager.Create(channel, topic)
        → mints run id  nullpointer_<yyyyMMdd_HHmmss>
        → makes videos/<channel>/<stamp>/ (+ images/ audio/ video/)
        → writes initial manifest.json   (all stages done:false)
        │
        ▼
 [1] ScriptAsync ─ Claude writes the script
        → claude CLI (Opus 4.8) over the channel brand + topic → ScriptDocument
        → PhoneticNarrationCleaner fills NarrationSpoken for TTS-risky words
        → writes script.json ; manifest.stages.script.done = true
        ⏸  CHECKPOINT: stops until review.scriptApproved  (Auto-run auto-approves)
        │
        ▼   (approve via:  ytpipe approve --run latest --stage script   /   UI "Approve ▶")
 [2] VoiceAsync ─ ElevenLabs speaks it
        → POST /with-timestamps  with NarrationForVoice()  (SpokenText of every beat, in order)
        → writes audio/voice.mp3  +  audio/align.json (per-char timings)
        → manifest.stages.voice.done = true , seconds = <duration>
        │
        ▼
 [3] ImagesAsync ─ Codex draws one frame per beat
        → codex CLI once over the whole script + channel image rule (<id>.image.md)
        → writes images/beat<NN>_<SS>_<label>.png  (4K, one per beat; retries 3×)
        → manifest.stages.images.done = true , count = <N>
        │
        ▼
 [4] VideoAsync ─ FFmpeg assembles + (optionally) scores it
        → TryAlignedDurations: map each beat → its spoken span (align.json) → per-image holds
        → MusicSelector (if channel.Music): Claude picks tracks → playlist covers the length
        → FFmpeg muxes frames + voice (+ music bed, faded) → video/final.mp4 (4K H.264/AAC)
        → if no thumbnail yet: Codex renders thumbnail.png
        → fills manifest.publish (title/description/tags) ; stages.video.done = true
        →                                                   stages.video.musicTracks = [...]
        ⏸  CHECKPOINT: stops until review.videoApproved  (Auto-run auto-approves, then STOPS)
        │
        ▼   (approve via:  ytpipe approve --run latest --stage video   /   UI "Approve ▶")
 [5] UploadAsync ─ ships to YouTube   (manual, optional)
        → OAuth (client secrets + cached token) → upload final.mp4 + publish metadata
        → manifest.stages.upload.done = true , videoId , privacy (default: private)
        │
        ▼
     Published (or private) on YouTube.
```

**Key properties of the flow:**

- **Manifest-driven & resumable.** Every step reads `manifest.json`, does its work, writes
  progress back. Re-running a stage (e.g. `ytpipe video --run latest`, or the UI's "Re-assemble")
  recomputes from current state — safe to repeat, and you can stop/resume at any stage.
- **Two hard gates.** The run will not pass *script* or *video* until the matching
  `review.*Approved` flag is set. `RequireApproved`/`StopForReview` enforce this; `Approve()` sets
  the flag. **Auto-run** (`AutoThroughVideoAsync`) runs 1→4 and flips both flags automatically, but
  still **stops before upload** — upload is always a deliberate act.
- **Voice precedes images on purpose.** Image hold-times are anchored to the real voiceover via
  `align.json`, so the audio must exist first. Without alignment the video stage falls back to a
  proportional word-count split.
- **Non-blocking extras.** Music and upload never abort a run: if music is disabled or selection
  fails, the video renders voice-only; upload is opt-in.
- **Same core, two faces.** In the web UI each step is a **background job** (`JobService`) with a
  live log the browser polls; the CLI runs the step inline. Both call identical `Pipeline` methods.

---

## 3. Solution layout

Solution file: `YtPipeline.slnx`. Targets **.NET 9**.

```
src/
  YtPipeline.Core      Domain models, abstractions (interfaces), RunManager, ChannelLoader,
                       validation, JSON helpers. No external-tool knowledge.
  YtPipeline.Stages    The five stage implementations + ProcessRunner + music selector +
                       Orchestration/Pipeline + shared DI (PipelineServiceRegistration).
  YtPipeline.Cli       Console entry point. Assembly name: `ytpipe`. System.CommandLine.
  YtPipeline.Web       ASP.NET Core minimal-API host that wraps Pipeline and serves the SPA.
tests/
  YtPipeline.Tests     xUnit tests.
web/                   React 19 + Vite + TS + Tailwind front-end (builds into Web/wwwroot).
channels/              Per-channel config: <id>.json (+ optional <id>.image.md rule).
videos/                Generated per-run output (gitignored).
logs/                  Serilog rolling logs (gitignored).
apply_music.py         Utility: re-run music selection + re-render an existing run's video.
publish-app.cmd        Builds the self-contained desktop app (app\YtPipeline.exe).
```

### Project dependencies

```
Cli  ─┐
Web  ─┼─► Stages ─► Core
Tests ┘            ▲
                   └── (Stages also references Core)
```

`Core` defines the contracts; `Stages` implements them and orchestrates; `Cli` and `Web` are
thin hosts that call `services.AddYtPipeline(...)` and invoke `Pipeline`.

---

## 4. The run model & on-disk layout

A **run** is one video. `Run` (in `Core/Models/Run.cs`) computes every artifact path from the
workspace root, channel, and timestamp. Everything for a run lives in one folder:

```
videos/<channel>/<timestamp>/
  manifest.json        single source of truth (stage progress, review state, publish metadata)
  script.json          the generated ScriptDocument
  images/              beat<NN>_<SS>_<label>.png frames from Codex
  audio/
    voice.mp3          the ElevenLabs voiceover
    align.json         per-character timestamps (AudioAlignment) for precise image sync
  video/
    concat.txt         FFmpeg concat list (image → duration)
    final.mp4          the finished 4K H.264 + AAC video
  thumbnail.png        YouTube thumbnail (generated during the video stage)
```

`RunManager` (`Core/Services/RunManager.cs`) owns run creation, id generation, and manifest /
script read-write. `Resolve("latest")` finds the most recently modified `manifest.json` across
all channels — that's what `--run latest` uses.

### Manifest shape (`Core/Models/Manifest.cs`)

```jsonc
{
  "runId": "nullpointer_20260607_182439",
  "channel": "nullpointer",
  "topic": "...",
  "createdUtc": "2026-06-07T18:24:39Z",
  "stages": {
    "script": { "done": true, "path": "...", "completedUtc": "..." },
    "images": { "done": true, "count": 179, "dir": "..." },
    "voice":  { "done": true, "seconds": 282.6 },
    "video":  { "done": true, "musicTracks": ["midnight","sable","falling","once was"] },
    "upload": { "done": false }
  },
  "review":  { "scriptApproved": true, "videoApproved": true },
  "publish": { "title": "...", "description": "...", "tags": ["..."], "hasThumbnail": true }
}
```

Each `StageStatus` has typed common fields (`done`, `path`, `completedUtc`) plus a
`[JsonExtensionData]` bag for stage-specific extras (`count`, `seconds`, `dir`, `videoId`,
`musicTracks`, …) that round-trips cleanly.

### Script model (`Core/Models/ScriptDocument.cs`, `ScriptBeat.cs`)

- `ScriptDocument` = `Title`, `Description`, `Tags`, and an ordered list of **beats**.
- A **beat** is one spoken segment paired with the one image drawn over it:
  - `Narration` — human-readable spoken text (shown in the UI).
  - `NarrationSpoken` — optional phonetic respelling for TTS-risky words (names, foreign terms).
  - `SpokenText` — computed: the respelling if present, else the narration. **This is what gets
    voiced *and* what the video stage uses for char-offset alignment**, so the two never drift.
  - `ImagePrompt` — what Codex draws (the channel visual style is prepended at generation time).
- `NarrationForVoice()` joins every beat's `SpokenText` in order — and *only* that. Titles,
  descriptions, and image prompts are never sent to TTS.

### Channel model (`Core/Models/Channel.cs`)

Loaded from `channels/<id>.json`. Carries brand identity (persona, audience, tone, visual style,
script structure, target length) and YouTube defaults. Notable fields:

- `Music` (bool, default **true**) — set `"music": false` in the channel JSON to ship voice-only
  videos. (Caveman Finance is voice-only.)
- `ImageInstructions` — *not* in the JSON; `ChannelLoader` loads it from the sibling
  `channels/<id>.image.md` if present, else falls back to `VisualStyle`.

---

## 5. The five stages

All stage implementations live in `src/YtPipeline.Stages`, behind interfaces in
`Core/Abstractions`. `Orchestration/Pipeline.cs` is the conductor.

### Stage 1 — Script (`Script/ClaudeCliScriptGenerator.cs`)
- Shells out to the **`claude`** CLI (`-p --model claude-opus-4-8 --effort high`), piping the
  prompt on stdin, and parses the first JSON object from stdout into a `ScriptDocument`.
- Scripts contain **no persona/channel branding** and target **8–10 min of voiceover**
  (~1300–1600 words) split into **many short one-visual-idea beats** (~4–9 words each, never
  >10), so each beat maps to exactly one image and can be word-accurately timed.
- After generation, `PhoneticNarrationCleaner` (also Claude) fills `NarrationSpoken` for beats
  with mispronounceable words — done once, before voicing, so no audio is regenerated.
- Validated by `ScriptDocumentValidator` (FluentValidation).
- **Checkpoint:** pipeline stops here until the script is approved.

### Stage 2 — Voice (`Voice/ElevenLabsVoiceGenerator.cs`)
- Calls the ElevenLabs **`/with-timestamps`** endpoint (Refit client `IElevenLabsApi`) with
  `NarrationForVoice()`, model `eleven_multilingual_v2`.
- Decodes the base64 audio → `audio/voice.mp3`, and saves per-character timings →
  `audio/align.json` (`AudioAlignment`). Duration is probed by `Mp3Duration`.
- Runs **before** images so the image stage can sync to real spoken timings.

### Stage 3 — Images (`Images/CodexCliImageGenerator.cs`)
- Runs the **`codex`** CLI **once** over the whole script
  (`exec --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox -`, prompt on stdin).
  Codex's built-in image tool writes PNGs that are collected into the run's `images/` folder.
- The prompt includes the channel's image rule (`channels/<id>.image.md`, e.g. the MS-Paint
  stick-figure rule for NullPointer), which says **exactly one image per beat**, named
  `beat<NN>_<SS>_<label>.png`. `ImageSet` parses `beat<NN>` back out for timing.
- Source frames are **4K (3840×2160)**. Retries up to 3× on transient failures, clearing the
  images dir before each attempt to avoid duplicate piles.
- `… images --run latest --dry-run` prints the exact Codex prompt without spending generations.

### Stage 4 — Video (`Video/FfmpegVideoAssembler.cs`)
- Builds per-image hold times, then muxes the image sequence + voice (+ optional music) into a
  **4K H.264 + AAC mp4** via **FFmpeg**.
- **Timing:** `TryAlignedDurations` maps each beat's char-offset in the narration to its spoken
  time (from `align.json`) and splits that beat's span across its images — precise sync. Falls
  back to `BuildDurations` (proportional by word count) if alignment is missing or images aren't
  all beat-labeled.
- **Music** (see §6): if the channel has `music: true` and a library is configured, a playlist is
  selected and mixed under the voice at low volume with a fade-out; the last track loops
  (`-stream_loop -1`) to cover any remainder. Voice is boosted (`VoiceGain`, default 1.25×) and
  `amix … normalize=0` keeps each stream at its own level.
- Also generates `thumbnail.png` (Codex) if missing, and fills `manifest.publish`
  (title/description/tags) — so title + description + thumbnail are publish-ready.
- **Checkpoint:** pipeline stops here until the video is approved.

### Stage 5 — Upload (`Upload/YouTubeUploader.cs`)
- Uploads `final.mp4` to YouTube with the publish metadata, defaulting to **private**.
- Uses OAuth client secrets (`YOUTUBE_CLIENT_SECRETS`); tokens cached in `.youtube-tokens/`.
- Optional — skipped while testing.

---

## 6. Background music subsystem

Music is mixed in **Stage 4**. Two pieces:

**Selection — `Music/MusicSelector.cs`:**
1. Skip entirely if `Music.Enabled` is false, the channel has `music: false`, or the library
   path is empty/missing — the video is assembled without music and the run continues.
2. Enumerate audio files (`.mp3/.wav/.m4a/.flac`) in `Music.LibraryPath`, strip filename noise
   (`massobeats - thoughts (freetouse.com)` → `thoughts`).
3. Ask **Claude** to rank *N* tracks by how well their name/vibe fits the script's title +
   opening/closing narration (N sized to over-cover the video at a 120s/track floor).
4. Resolve Claude's names back to files (exact, then fuzzy), probe real durations, and keep just
   enough tracks to cover the audio length. Pad from the rest of the library if still short.
5. Any failure → empty list → video renders without music (never blocks a run).

**Mixing — `Video/FfmpegVideoAssembler.cs`:** normalizes each track to 48 kHz stereo, concatenates
multiple tracks, applies `volume` + `afade=out`, and `amix`es with the boosted voice.

**Config (`Music` section):** `Enabled`, `LibraryPath`, `Volume` (default 0.15), `FadeOutSeconds`
(default 3.0). The selected track names are persisted to `manifest.stages.video.musicTracks` and
displayed in the web UI's Video card.

**`apply_music.py`** (repo root) replicates this exact algorithm in Python to retro-fit music onto
an already-rendered run: it reads the run's `script.json`/`manifest.json`, runs the same Claude
selection + duration-coverage logic, re-renders the video with the same FFmpeg filter graph, and
writes `musicTracks` back to the manifest. Used when a video was rendered voice-only by mistake.

---

## 7. Orchestration & cross-cutting services

- **`Orchestration/Pipeline.cs`** — public methods per stage (`ScriptAsync`, `VoiceAsync`,
  `ImagesAsync`, `VideoAsync`, `ThumbnailAsync`, `UploadAsync`), plus:
  - `RunAllAsync` — full run, honoring both review checkpoints.
  - `AutoThroughVideoAsync` — "Auto-run": script → voice → images → video, **auto-approving**
    both checkpoints, stopping before upload.
  - `Approve(run, stage)` and the `RequireApproved` / `StopForReview` checkpoint guards.
- **`Process/ProcessRunner.cs`** — runs external CLIs with **UTF-8** stdin/stdout/stderr (fixes
  em-dash corruption from OEM-codepage decoding of `claude`/`codex` output).
- **`Process/NetworkGuard.cs`** — network-related guarding for subprocess calls.
- **`Core/Services/Json.cs`** — Newtonsoft read/write helpers; writes **BOM-less UTF-8**.
- **`Core/Services/ChannelLoader.cs`** — loads `channels/<id>.json` + the `<id>.image.md` rule.
- **DI — `PipelineServiceRegistration.AddYtPipeline`** — the single composition root shared by
  CLI and Web: registers `RunManager`, `ChannelLoader`, `ProcessRunner`, the validator, all stage
  options (binding config sections, clearing list defaults first so CLI args don't duplicate),
  every stage implementation, `Pipeline`, and the Refit ElevenLabs client. Logging is left to each
  host. Secrets come from environment variables (load `.env` first).

---

## 8. CLI host (`YtPipeline.Cli`, assembly `ytpipe`)

`Program.cs` loads `.env` (walking up parents), builds config from `appsettings.json` + env,
configures Serilog (console + rolling file under `logs/`), calls `AddYtPipeline`, and registers
System.CommandLine subcommands. Run from the repo root so `channels/` and `videos/` resolve.

```bash
# full run (stops at the script and video checkpoints)
ytpipe run     --channel nullpointer --topic "How DNS actually works"

# one stage at a time (resumes from the manifest)
ytpipe script  --channel nullpointer --topic "..."         # --dry-run prints the prompt only
ytpipe images  --run latest                                 # --dry-run prints the Codex prompt
ytpipe voice   --run latest
ytpipe video   --run latest
ytpipe upload  --run latest --privacy private

# approve a checkpoint so the pipeline can continue
ytpipe approve --run latest --stage script   # or --stage video
```

(During development use `dotnet run --project src/YtPipeline.Cli -- <args>`.)

---

## 9. Web host (`YtPipeline.Web`) + React UI (`web/`)

**Backend** (`Program.cs`, `Jobs.cs`): a minimal-API wrapper around `Pipeline`, listening on
`http://localhost:5080`. It finds the workspace root by walking up from cwd and the exe dir to the
folder containing `channels/`, serves the built SPA from `wwwroot` (with `index.html` set
`no-cache`), and runs stages as **background jobs** with live log streaming.

Key endpoints:

| Method & path | Purpose |
|---|---|
| `GET /api/channels` | list channels |
| `GET /api/runs` | all runs (manifests), newest first |
| `GET /api/runs/{id}` | run detail: manifest, script, image list, has-audio/video/thumbnail |
| `POST /api/script` | create a run + start script (or auto) job |
| `POST /api/runs/{id}/stage/{stage}` | start images/voice/video/thumbnail/upload as a job |
| `POST /api/runs/{id}/approve` | approve the script or video checkpoint |
| `DELETE /api/jobs/{jobId}` | cancel a running job |
| `GET /api/jobs/{jobId}?since=N` | job status + incremental logs |
| `GET /api/runs/{id}/{image\|audio\|video\|thumbnail}` | serve artifacts |

`Jobs.cs` = `JobService` + an `AsyncLocal` `JobLoggerProvider` that captures per-job logs; the UI
polls `/api/jobs/{id}?since=N`. Multiple runs/jobs run concurrently.

**Frontend** (`web/`): **React 19.2 + Vite 7 + TypeScript + Tailwind v4**, with **TanStack Query**
(server state/polling), **zustand** (per-run job tracking), **axios**, **lucide-react**, and
`cva`/`clsx`/`tailwind-merge`. Vite builds into `src/YtPipeline.Web/wwwroot` (gitignored).

- Components: `App`, `NewVideo`, `RunList`, `RunDetail`, `StageCard`, `JobLog`, `ui`.
- `lib/api.ts` mirrors the API response types and call surface; `lib/hooks.ts` wraps them in
  TanStack Query hooks; `lib/store.ts` is the zustand job store.
- `RunDetail` renders the five stage cards, the image gallery / audio / video players, the
  **Publish details** panel (thumbnail, title, description, tags), the **background-music** pill
  list (from `manifest.stages.video.musicTracks`), and the **Approve** buttons. Action buttons are
  disabled while a job runs; an **Auto-run** toggle drives `AutoThroughVideoAsync`.

**Dev (two terminals):**
```bash
dotnet run --project src/YtPipeline.Web      # API on :5080
npm --prefix web install && npm --prefix web run dev   # Vite on :5173, proxies /api → :5080
```

**Desktop app:** `publish-app.cmd` builds the React app into `wwwroot` and packages a single
self-contained `app\YtPipeline.exe` (win-x64, no .NET/Node needed). It starts the server, serves
the SPA, and opens the browser (only when not in Development). A Desktop shortcut `YtPipeline.lnk`
(WorkingDirectory = `app\`) launches it. `app/` is gitignored.

---

## 10. Configuration & secrets

**Non-secret config** lives in `appsettings.json` (the CLI's and Web's are kept in sync):

| Section | Key settings |
|---|---|
| `Workspace` | `Root` (blank = auto-detect), `ChannelsDir` |
| `Claude` | `Executable` (`claude`), `Args` (`-p --model claude-opus-4-8 --effort high`) |
| `Codex` | full `Executable` path, `ArgsTemplate`, `Width/Height` (3840×2160), `WarnImageCount` |
| `ElevenLabs` | `BaseUrl`, `ModelId` (`eleven_multilingual_v2`), `Stability`, `SimilarityBoost` |
| `Ffmpeg` | `Executable` (`D:\Tools\ffmpeg\ffmpeg.exe`), `Width/Height/Fps/Crf/Preset`, `VoiceGain` |
| `Music` | `Enabled`, `LibraryPath`, `Volume` (0.15), `FadeOutSeconds` (3.0) |
| `YouTube` | `ClientSecretsPath`, `TokenStoreDir` |

> The config binder *appends* to existing `List<T>` values, so `AddYtPipeline` clears the
> constructor defaults of `Claude.Args` / `Codex.ArgsTemplate` before binding — otherwise CLI
> args would be duplicated.

**Secrets** come from `.env` (gitignored; copy from `.env.example`) or environment variables:
`ELEVENLABS_API_KEY`, `ELEVENLABS_VOICE_ID`, `YOUTUBE_CLIENT_SECRETS`. `claude` and `codex` use
their own CLI logins — no keys for them live in this repo.

---

## 11. Build, test, and environment

```bash
dotnet build      # build the solution
dotnet test       # run xUnit tests (tests/YtPipeline.Tests)
```

**External tools required on PATH** (the pipeline shells out to these): `claude`, `codex`,
`ffmpeg`. Verified working versions: .NET 9 SDK, `claude` 2.1.x, `codex` 0.137.0, FFmpeg 8.1.1.

**Codex invocation gotchas** (codex 0.137.0): `--full-auto` is not valid; the project isn't a git
repo so `--skip-git-repo-check` is required; the image tool needs network + file-write so the
bypass flag is required; a huge prompt as a CLI arg breaks on Windows (must pipe on **stdin** via
the trailing `-`); `Codex.Executable` must be the **full path** (bare `codex` mis-resolves under
.NET `ProcessStartInfo`).

**Environment notes:** Windows / PowerShell 5.1. FFmpeg lives at `D:\Tools\ffmpeg\ffmpeg.exe`.
Keep large outputs on **D:** — the C: drive runs critically low. JSON is written BOM-less.

---

## 12. Channels

Channels are configured in `channels/<id>.json` (+ optional `channels/<id>.image.md` image rule,
with `channels/_default.image.md` as the shared fallback). Current channels:

| Id | Name | Music | Notes |
|---|---|---|---|
| `nullpointer` | NullPointer | yes | Tech explainers ("Ptr the wizard"), MS-Paint stick-figure style |
| `cavefinance` | Caveman Finance | **no** (voice-only) | Finance, caveman theme |
| `doodledamage` | Doodle Damage | yes | — |
| `testmini` | Test Mini | — | tiny end-to-end test channel |

---

## 13. Pipeline invariants (quick reference)

- One run = one folder; `manifest.json` is the only source of truth.
- Stages are resumable/idempotent — re-running a stage recomputes from the manifest.
- Execution order: **script → voice → images → video → upload**.
- Two human checkpoints (script, video); Auto-run auto-approves both, stops before upload.
- Only `SpokenText` is voiced & aligned; one image per beat; images named `beat<NN>_<SS>_*.png`.
- Output is 4K (3840×2160) H.264 + AAC. Music (if any) is mixed under the voice with a fade-out.
- Upload defaults to **private**. Music/upload never block a run if they fail or are disabled.
```
