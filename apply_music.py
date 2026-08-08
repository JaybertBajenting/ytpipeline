"""
apply_music.py — runs the exact MusicSelector algorithm for a run that was
assembled without music, then re-renders the video with the selected tracks.

Usage: python apply_music.py
"""

import json, os, re, subprocess, sys, struct, math
from pathlib import Path

# ── Config (mirrors appsettings.json) ────────────────────────────────────────
WORKSPACE    = Path(r"D:\Users\M4tth3w\Downloads\Youtube Pipeline Plan")
RUN_DIR      = WORKSPACE / "videos" / "cavefinance" / "20260608_210117"
LIBRARY      = Path(r"D:\Users\M4tth3w\Downloads\Massobeats")
FFMPEG       = Path(r"D:\Tools\ffmpeg\ffmpeg.exe")
PYTHON       = Path(r"C:\Users\M4tth3w\AppData\Local\Programs\Python\Python313\python.exe")

MUSIC_VOLUME    = 0.15
FADE_OUT_SECS   = 3.0
VOICE_GAIN      = 1.25
WIDTH, HEIGHT   = 3840, 2160
FPS             = 30
CRF             = 20
PRESET          = "medium"
MIN_TRACK_SECS  = 120.0   # conservative floor for estimating how many tracks to request

CLAUDE_EXE  = "claude"
CLAUDE_ARGS = ["-p", "--model", "claude-opus-4-8", "--effort", "high"]

AUDIO_EXTS = {".mp3", ".wav", ".m4a", ".flac"}

# ── Helpers ───────────────────────────────────────────────────────────────────

def strip_label(name: str) -> str:
    """'massobeats - thoughts (freetouse.com)' → 'thoughts'"""
    p = name.find("(")
    if p > 0:
        name = name[:p].strip()
    d = name.find(" - ")
    if d >= 0:
        name = name[d + 3:].strip()
    return name


def mp3_duration_seconds(path: Path) -> float:
    """Pure-Python MP3 duration via the av library (same as the app uses)."""
    try:
        import av
        with av.open(str(path)) as container:
            return float(container.duration) / 1_000_000
    except Exception:
        return 180.0   # 3-min fallback if probing fails


def build_prompt(title: str, opening: str, closing: str,
                 short_names: list[str], n_request: int) -> str:
    lines = [
        "You are building a background music playlist for a YouTube video.",
        "The music plays softly under the voiceover — it should fit the emotional tone and topic.",
        "",
        f'Video title: "{title}"',
    ]
    if opening:
        lines.append(f'Opening narration: "{opening}"')
    if closing and closing != opening:
        lines.append(f'Closing narration: "{closing}"')
    lines += [
        "",
        f"Pick exactly {n_request} DIFFERENT tracks from the list below, ordered from best fit to least.",
        "Track 1 will play first, track 2 will continue after it ends, and so on — so choose tracks",
        "whose moods flow naturally from one to the next (no jarring mood shifts mid-video).",
        "",
        "Available tracks (lo-fi / chill beat names — use the vibe of the name to match the video's mood):",
    ]
    for n in short_names:
        lines.append(f"- {n}")
    lines += [
        "",
        f"Return ONLY this JSON (no prose, no markdown, no explanation) with exactly {n_request} entries:",
        '{"tracks": ["<track1>", "<track2>", ...]}',
        "Use the exact names from the list above.",
    ]
    return "\n".join(lines)


def extract_first_json(text: str) -> dict | None:
    start = text.find("{")
    if start < 0:
        return None
    depth = 0
    for i, ch in enumerate(text[start:], start):
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                try:
                    return json.loads(text[start:i + 1])
                except json.JSONDecodeError:
                    return None
    return None


def resolve_track(files: list[Path], short_names: list[str], chosen: str) -> Path | None:
    chosen_l = chosen.lower()
    # exact match first
    for f, s in zip(files, short_names):
        if s.lower() == chosen_l:
            return f
    # fuzzy
    for f, s in zip(files, short_names):
        if chosen_l in s.lower() or s.lower() in chosen_l:
            return f
    return None


# ── Step 1: read script & manifest ───────────────────────────────────────────
print("Reading script and manifest…")
script   = json.loads((RUN_DIR / "script.json").read_text(encoding="utf-8"))
manifest = json.loads((RUN_DIR / "manifest.json").read_text(encoding="utf-8"))

title   = script["title"]
beats   = script["beats"]
opening = beats[0]["narration"].strip() if beats else ""
closing = beats[-1]["narration"].strip() if beats else ""

audio_seconds = manifest["stages"]["voice"]["seconds"]
print(f"  Title   : {title}")
print(f"  Opening : {opening}")
print(f"  Closing : {closing}")
print(f"  Duration: {audio_seconds}s")

# ── Step 2: enumerate music library ──────────────────────────────────────────
print("\nScanning music library…")
files = sorted(
    (p for p in LIBRARY.iterdir() if p.suffix.lower() in AUDIO_EXTS),
    key=lambda p: p.name.lower()
)
short_names = [strip_label(p.stem) for p in files]
print(f"  Found {len(files)} tracks: {', '.join(short_names)}")

# ── Step 3: ask Claude to rank tracks ─────────────────────────────────────────
n_request = min(len(files), max(2, math.ceil(audio_seconds / MIN_TRACK_SECS) + 1))
print(f"\nAsking Claude to pick {n_request} tracks…")

prompt = build_prompt(title, opening, closing, short_names, n_request)
result = subprocess.run(
    [CLAUDE_EXE] + CLAUDE_ARGS,
    input=prompt, text=True, capture_output=True, encoding="utf-8"
)
if result.returncode != 0:
    print("Claude stderr:", result.stderr[:500])
    sys.exit(f"Claude exited with code {result.returncode}")

response = extract_first_json(result.stdout)
if not response or not response.get("tracks"):
    sys.exit(f"Could not parse Claude response:\n{result.stdout[:500]}")

print(f"  Claude chose: {response['tracks']}")

# ── Step 4: resolve names → file paths ───────────────────────────────────────
resolved: list[Path] = []
for chosen in response["tracks"]:
    p = resolve_track(files, short_names, chosen.strip())
    if p and p not in resolved:
        resolved.append(p)

if not resolved:
    sys.exit("None of Claude's track names matched any file in the library.")

# ── Step 5: build playlist (cover audio length, last track loops) ─────────────
print("\nProbing durations and building playlist…")
playlist: list[Path] = []
covered = 0.0
for p in resolved:
    playlist.append(p)
    dur = mp3_duration_seconds(p)
    covered += dur
    print(f"  + {strip_label(p.stem):30s}  {dur:.0f}s  (covered {covered:.0f}s / {audio_seconds:.0f}s needed)")
    if covered >= audio_seconds:
        break

# Pad with more library tracks if still short
if covered < audio_seconds:
    for p in files:
        if p in playlist:
            continue
        playlist.append(p)
        dur = mp3_duration_seconds(p)
        covered += dur
        print(f"  + {strip_label(p.stem):30s}  {dur:.0f}s  (covered {covered:.0f}s — padding)")
        if covered >= audio_seconds:
            break

track_names = [strip_label(p.stem) for p in playlist]
print(f"\nPlaylist ({len(playlist)} track{'s' if len(playlist) != 1 else ''}): {' -> '.join(track_names)}")

# ── Step 6: build FFmpeg command ───────────────────────────────────────────────
print("\nBuilding FFmpeg command…")
audio_path  = RUN_DIR / "audio" / "voice.mp3"
concat_path = RUN_DIR / "video" / "concat.txt"
video_path  = RUN_DIR / "video" / "final.mp4"

vf = (f"scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=decrease,"
      f"pad={WIDTH}:{HEIGHT}:(ow-iw)/2:(oh-ih)/2:color=black,"
      f"setsar=1,fps={FPS}")

args = [str(FFMPEG), "-y"]
args += ["-f", "concat", "-safe", "0", "-i", str(concat_path)]   # input 0: video frames
args += ["-i", str(audio_path)]                                    # input 1: voice

for t, p in enumerate(playlist):
    if t == len(playlist) - 1:
        args += ["-stream_loop", "-1"]   # loop last track
    args += ["-i", str(p)]              # inputs 2, 3, 4, …

args += ["-vf", vf]

# Build filter_complex
fade_start = max(0.0, audio_seconds - FADE_OUT_SECS)
vol_str   = f"{MUSIC_VOLUME:.2f}".rstrip("0").rstrip(".")
fade_str  = f"{fade_start:.3f}"
dur_str   = f"{FADE_OUT_SECS:.1f}"
vgain_str = f"{VOICE_GAIN:.3f}"

mf_parts = []
for t in range(len(playlist)):
    mf_parts.append(f"[{t + 2}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[m{t}]")

if len(playlist) == 1:
    bgm_source = "[m0]"
else:
    labels = "".join(f"[m{t}]" for t in range(len(playlist)))
    mf_parts.append(f"{labels}concat=n={len(playlist)}:v=0:a=1[chain]")
    bgm_source = "[chain]"

mf_parts.append(f"{bgm_source}volume={vol_str},afade=t=out:st={fade_str}:d={dur_str}[bgm]")
mf_parts.append(f"[1:a]volume={vgain_str},aformat=channel_layouts=stereo[voc]")
mf_parts.append("[voc][bgm]amix=inputs=2:duration=first:dropout_transition=2:normalize=0[aout]")

filter_complex = ";".join(mf_parts)
args += ["-filter_complex", filter_complex, "-map", "0:v", "-map", "[aout]"]
args += ["-c:v", "libx264", "-preset", PRESET, "-crf", str(CRF)]
args += ["-pix_fmt", "yuv420p"]
args += ["-c:a", "aac", "-b:a", "192k"]
args += ["-shortest"]
args.append(str(video_path))

print("  FFmpeg command ready.")

# ── Step 7: run FFmpeg ─────────────────────────────────────────────────────────
print(f"\nRendering video (this may take several minutes for a {audio_seconds:.0f}s video)…")
proc = subprocess.run(args, capture_output=True, text=True)
if proc.returncode != 0:
    print("FFmpeg stderr (last 2000 chars):")
    print(proc.stderr[-2000:])
    sys.exit(f"FFmpeg failed with code {proc.returncode}")

print("  Render complete.")

# ── Step 8: update manifest ────────────────────────────────────────────────────
print("\nUpdating manifest…")
manifest["stages"]["video"]["musicTracks"] = track_names
manifest_path = RUN_DIR / "manifest.json"
manifest_path.write_bytes(json.dumps(manifest, indent=2, ensure_ascii=False).encode("utf-8"))
print(f"  Saved: musicTracks = {track_names}")

print("\n✓ Done! Video re-rendered with background music.")
