# YtPipeline

Multi-channel YouTube automation pipeline built to [`YtPipeline-Spec.md`](YtPipeline-Spec.md).
A .NET 9 orchestrator that chains five stages — **script → voice → images → video → upload** —
with human review checkpoints, one resumable **run** at a time. Driven from a CLI or a React web UI.

> For a deep dive (data model, every stage, services, API), see [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Layout

```
src/YtPipeline.Core     domain models, abstractions, RunManager, ChannelLoader, validation
src/YtPipeline.Stages   the five stage implementations + ProcessRunner + music + Pipeline + DI
src/YtPipeline.Cli       CLI entry point (ytpipe), commands, appsettings.json
src/YtPipeline.Web       ASP.NET Core minimal API that wraps Pipeline and serves the SPA
web/                     React 19 + Vite + TS + Tailwind front-end (builds into Web/wwwroot)
channels/                per-channel context (nullpointer, cavefinance, doodledamage, …)
videos/  logs/           generated per run (gitignored)
tests/                   xUnit tests
```

A **run** is keyed `{channel}_{yyyyMMdd_HHmmss}`. Everything for one video lives in a
single folder, grouped by channel:

```
videos/<channel>/<timestamp>/
  manifest.json       single source of truth (progress/state, publish metadata)
  script.json         the generated script
  images/             beat<NN>_<SS>_*.png from Codex
  audio/voice.mp3     the voiceover
  audio/align.json    per-character timestamps for precise image sync
  video/final.mp4     the finished 4K video
  thumbnail.png       the YouTube thumbnail
```

Each stage reads the manifest, does its work, and updates it — so stages are resumable
and idempotent.

## Setup

1. `cp .env.example .env` and fill in the ElevenLabs key/voice and YouTube client secrets path.
2. Stages 1, 2, 4 shell out to external tools that must be on PATH: `claude`, `codex`, `ffmpeg`.
   `claude` / `codex` use their own login; no keys live in this repo.
3. Build: `dotnet build`  ·  Test: `dotnet test`

## Usage

Run all commands from the repo root (so `channels/` and `videos/` resolve).

```bash
# full run (stops at the script and video review checkpoints)
dotnet run --project src/YtPipeline.Cli -- run --channel nullpointer --topic "How DNS actually works"

# one stage at a time (resumes from the manifest)
dotnet run --project src/YtPipeline.Cli -- script  --channel nullpointer --topic "..."
dotnet run --project src/YtPipeline.Cli -- script  --channel nullpointer --topic "..." --dry-run   # print prompt only
dotnet run --project src/YtPipeline.Cli -- images  --run latest
dotnet run --project src/YtPipeline.Cli -- voice   --run latest
dotnet run --project src/YtPipeline.Cli -- video   --run latest
dotnet run --project src/YtPipeline.Cli -- upload  --run latest --privacy private

# approve a checkpoint so the pipeline can continue
dotnet run --project src/YtPipeline.Cli -- approve --run latest --stage script
```

After `dotnet build`, the executable is named `ytpipe` (e.g. `ytpipe run --channel ...`).

## Web UI

A **React 19 + Vite + Tailwind** frontend (in `web/`) talks to the ASP.NET Core **API**
(`src/YtPipeline.Web`). State/data via TanStack Query + zustand, axios for HTTP.

- **Dev:** two terminals —
  1. API: `dotnet run --project src/YtPipeline.Web` (http://localhost:5080)
  2. Frontend: `npm --prefix web install` then `npm --prefix web run dev` (http://localhost:5173, proxies `/api` to 5080)
- **As a desktop app:** run `publish-app.cmd` — it builds the React app into the API's
  `wwwroot` and packages a single self-contained `app\YtPipeline.exe` (no .NET/Node needed to
  run). Double-click it (or the Desktop shortcut); it starts the server, serves the built
  React app, and opens your browser. Close the console window to stop.
- The exe finds the repo root (the folder with `channels/`) by walking up from its location,
  loads `.env` from there, and serves `index.html` with `no-cache` so new builds always load.

The UI lists channels and runs, generates scripts (Opus 4.8), runs each stage as a
background job with a live log, shows the image gallery / audio / video / selected background
music, and has the script + video approval buttons. Upload is optional and clearly marked.

Stage order: **script → voiceover → images → video → upload**. Scripts contain no persona
or channel branding and target 8–10 minutes of voiceover (~1300–1600 words). After the
video, a **thumbnail** is generated and the **title + description + tags** are shown in a
Publish panel, ready for YouTube. The **Auto-run** checkbox chains script → voice → images →
video and auto-approves the checkpoints (stops before upload) — for "just get it done" days.
Action buttons are disabled while a stage job is running.

## Image generation rule (Stage 3)

Stage 3 (images) hands Codex the whole script plus a per-channel rule, and lets Codex's built-in
image tool write the PNGs into the run's `images/` folder. The script stage produces **many short
one-visual-idea beats**, and the rule says generate **exactly one image per beat** — so each image
maps to one beat and can be word-accurately timed.

- The rule lives in `channels/<id>.image.md` (e.g. [`channels/nullpointer.image.md`](channels/nullpointer.image.md)),
  with `channels/_default.image.md` as the shared fallback. If neither exists, the channel's
  `visualStyle` is used.
- Codex is told to name files `beat<NN>_<SS>_<label>.png`. The video stage reads `beat<NN>` to map
  each image to its beat's spoken span (via `audio/align.json`) for precise sync; it falls back to
  a proportional word-count split when alignment is unavailable.
- Source frames are 4K (3840×2160). Preview the exact Codex prompt without spending image
  generations: `… images --run latest --dry-run`.

## Configuration

Non-secret settings live in [`src/YtPipeline.Cli/appsettings.json`](src/YtPipeline.Cli/appsettings.json)
(the Web host keeps a matching copy); secrets come from `.env`. Settings worth tuning per machine:

- **`Claude.Args`** — defaults to `["-p", "--model", "claude-opus-4-8", "--effort", "high"]`
  (non-interactive print mode); the prompt is piped on stdin.
- **`Codex.Executable`** — must be the **full path** to `codex.exe` (a bare `codex` mis-resolves
  under .NET `ProcessStartInfo`). `Codex.ArgsTemplate` defaults to
  `["exec", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "-"]` (prompt on stdin).
- **`Ffmpeg.Executable`** — absolute path to `ffmpeg.exe` (e.g. `D:\Tools\ffmpeg\ffmpeg.exe`).
- **`Music`** — `Enabled`, `LibraryPath`, `Volume` (0.15), `FadeOutSeconds` (3.0). See below.

### Background music (Stage 4)

If the channel allows music (`"music": true` in its JSON — the default) and a `Music.LibraryPath`
is set, the video stage asks Claude to pick tracks from the library that fit the script's tone,
sizes a playlist to cover the video length (the last track loops), and mixes them under the voice
at low volume with a fade-out. The chosen track names are saved to
`manifest.stages.video.musicTracks` and shown in the UI. Set `"music": false` on a channel for
voice-only videos. Music never blocks a run — if selection fails or it's disabled, the video
renders without it. `apply_music.py` re-runs this selection to retro-fit music onto an existing run.

### Google Drive (Move to Google Drive)

Set up in Settings → Storage. With an **rclone remote** set (`gdrive:YtPipeline`), a move uploads with
rclone, checks every file, and only then frees the space here. Without one, it copies into the Google
Drive for desktop folder instead. rclone's shared Google client ID is retired during 2026; when
uploads start failing with sign-in errors, follow
[docs/google-drive-own-client-id.md](docs/google-drive-own-client-id.md).

## Notes

- **Per-beat timing** is anchored to the actual voiceover: the voice stage captures ElevenLabs
  per-character timestamps (`audio/align.json`) and the video stage maps each beat to its exact
  spoken span. It falls back to a proportional word-count split when alignment is unavailable.
- External tools shell out via `ProcessRunner` with UTF-8 stdin/stdout (fixes em-dash corruption).
- YouTube upload defaults to `private` until proven.
- Targets .NET 9. Keep large outputs on D: — the C: drive runs low.
