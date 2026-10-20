# CLAUDE.md — YtPipeline

Multi-channel YouTube automation: a .NET 9 orchestrator chains five stages,
**script -> voice -> images -> video -> upload**, with human review checkpoints, one resumable run at a
time. Driven from a CLI (`ytpipe`) or a React web UI. Deep dive: `ARCHITECTURE.md`; original spec:
`YtPipeline-Spec.md`.

## Commands

Run from the repo root so `channels/` and `videos/` resolve.

```powershell
dotnet build
dotnet test                                          # xUnit + FluentAssertions + Moq (tests/YtPipeline.Tests)
dotnet run --project src/YtPipeline.Cli -- run --channel <id> --topic "..."
dotnet run --project src/YtPipeline.Cli -- script --channel <id> --topic "..." --dry-run   # print the prompt only
dotnet run --project src/YtPipeline.Cli -- images --run latest       # any single stage, resumed from the manifest
dotnet run --project src/YtPipeline.Cli -- approve --run latest --stage script
dotnet run --project src/YtPipeline.Web              # API on http://localhost:5080
npm --prefix web install; npm --prefix web run dev   # UI on http://localhost:5173, proxies /api
publish-app.cmd                                      # SPA into Web/wwwroot + self-contained app\YtPipeline.exe
```

## Layout

```
src/YtPipeline.Core     models (Manifest, ScriptDocument, Channel), abstractions, RunManager, ChannelLoader
src/YtPipeline.Stages   the five stages, ProcessRunner, music, Pipeline, DI
src/YtPipeline.Cli      `ytpipe` entry point and commands
src/YtPipeline.Web      minimal API around Pipeline; serves the SPA
web/                    React 19 + Vite + Tailwind + TanStack Query + zustand
channels/<id>/          per-channel context: voice, structure, branding, prompts
tests/YtPipeline.Tests  unit tests
videos/<channel>/<timestamp>/   one run: manifest.json, script.json, images/, audio/, video/, thumbnail.png
```

## Invariants

- One run = one folder; `manifest.json` is the only source of truth. Every stage reads it, does its work,
  and updates it, so stages are resumable and idempotent.
- Two human checkpoints (script, video). Auto-run approves both and stops before upload.
- Only `SpokenText` is voiced and aligned; one image per beat, named `beat<NN>_<SS>_*.png`.
- Output is 4K (3840x2160) H.264 + AAC; music is mixed under the voice with a fade-out.
- Upload defaults to **private**. Music and upload never block a run when they fail or are disabled.
- JSON is written BOM-less.

## Gotchas

- External tools must be installed: `claude`, `codex`, `ffmpeg`. `claude`/`codex` use their own login;
  no AI keys live in this repo.
- Codex: `--skip-git-repo-check` is required; the image tool needs network and file-write access; long
  prompts must go on **stdin** (trailing `-`), not as a command-line argument, on Windows; configure
  `Codex.Executable` as a full path (a bare `codex` mis-resolves under `ProcessStartInfo`).
- Windows / PowerShell 5.1. Keep large outputs on a drive with space; 4K renders are big.

## Conventions

- New behaviour gets a unit test; mock process calls through the abstractions in Core.
- Channel-specific wording belongs in `channels/<id>/`, not in stage code.

## Never

- Commit `.env` (ElevenLabs key, YouTube client secrets path), OAuth tokens, or anything under `videos/`
  and `logs/`.
