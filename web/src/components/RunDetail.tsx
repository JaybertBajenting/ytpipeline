import { useState, type ReactNode } from "react";
import {
  ArrowLeft, CalendarDays, Camera, Clock, Cloud, CloudDownload, CloudUpload, Download, ExternalLink,
  FolderOpen, MonitorPlay, Music, Rocket, Square, ThumbsUp, Trash2, VolumeX,
} from "lucide-react";
import {
  api, type ImageProvider, type Manifest, type ProviderHealth, type StageName, type StageStatus,
} from "@/lib/api";
import {
  useApprove, useCancelJob, useChannels, useDeleteImage, useDriveTransfer, useJob, useProviderHealth,
  useRevealVideo, useRun, useRunStage, useScheduleRun, useSettings,
} from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { channelVoiceModel, voiceModelLabel, voiceModelOptions } from "@/lib/voiceModels";
import { Badge, Button, SectionLabel, Spinner, Tag, selectCls } from "./ui";
import { StageCard } from "./StageCard";
import { JobLog } from "./JobLog";
import { DeleteVideoDialog } from "./DeleteVideoDialog";
import { MoveToDriveDialog } from "./MoveToDriveDialog";
import { cn } from "@/lib/utils";

/** Thumbnails rendered before the "show all" toggle — 90+ at once made the panel crawl. */
const IMAGE_PREVIEW_COUNT = 12;

export function RunDetail({ runId }: { runId: string }) {
  const localJobId = useUi((s) => s.jobs[runId]) ?? null;
  const runQ = useRun(runId, !!localJobId);

  // Trust the SERVER about what's running. A job started from another tab, the CLI, or before a
  // reload isn't in this browser's store, and treating that as idle is what made counters look
  // frozen and let disabled-looking buttons fire a second job.
  const serverJobId = runQ.data?.activeJob?.id ?? null;
  const jobId = localJobId ?? serverJobId;
  const job = useJob(jobId, runId);
  const busy = !!serverJobId || job?.status === "running";           // poll live while a job is running
  const stage = useRunStage(runId);
  const approve = useApprove(runId);
  const cancel = useCancelJob();
  const schedule = useScheduleRun();
  const deleteImage = useDeleteImage();
  const reveal = useRevealVideo();
  const drive = useDriveTransfer(runId);
  // Confirmed in a dialog: the move removes hundreds of MB from this PC, even though Bring back undoes it.
  const [moving, setMoving] = useState(false);
  // The server explains refusals ("set your Drive folder first", "a job is running") in `error`;
  // axios's own message would only say which status code came back.
  const driveError = drive.error
    ? ((drive.error as { response?: { data?: { error?: string } } }).response?.data?.error
      ?? (drive.error as Error).message)
    : null;
  const { data: settings } = useSettings();
  const { data: channels } = useChannels();
  const { data: providerHealth } = useProviderHealth();
  // Either way of reaching Drive will do: an rclone remote, or the Drive app's folder.
  const driveReady = !!(settings?.rcloneRemote || settings?.driveArchiveFolder);
  const [privacy, setPrivacy] = useState("private");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showAllImages, setShowAllImages] = useState(false);
  // Which frame's trash button has been clicked once. Deleting is cheap to undo (the next images
  // run regenerates it) but a stray click on a 200-tile grid is easy, so it takes two.
  const [armedFrame, setArmedFrame] = useState<string | null>(null);
  // Null until you touch the toggle, so the control shows what the last assembly actually did
  // rather than resetting to a default every time this view mounts.
  const [musicChoice, setMusicChoice] = useState<boolean | null>(null);
  // Same idea for the voice model: null until you pick, so it shows what this video last asked for.
  const [voicePick, setVoicePick] = useState<string | null>(null);

  // Read the provider from settings rather than hardcoding a name — the router resolves it at
  // call time, so a stale label here just makes people doubt their own configuration.
  // A channel can name its own generator, which wins over Settings.
  const provider = channels?.find((c) => c.id === runQ.data?.manifest.channel)?.imageProvider
    || settings?.imageProvider;
  const imageProvider = provider === "gflow"
    ? `gflow${settings?.gflow.model ? ` · ${settings.gflow.model}` : ""}`
    : provider === "codex" ? "Codex" : "…";

  const data = runQ.data;
  const bust = runQ.dataUpdatedAt;

  if (!data) return (
    <div className="flex h-64 items-center justify-center text-sm text-white/30">
      <Spinner className="mr-2" /> Loading…
    </div>
  );

  const { manifest: m, script, images, hasAudio, hasVideo, hasThumbnail } = data;
  const st = m.stages;
  const sa = m.review.scriptApproved;
  const va = m.review.videoApproved;
  // The images stage calls MarkDone even when it only produced some of the frames (gflow
  // returns successfully as long as at least one PNG exists), so st.images.done cannot be
  // trusted to mean "finished". Completeness is frames-on-disk vs the script's beat count.
  const expectedFrames = script?.beats.length ?? 0;
  const imagesPartial = expectedFrames > 0 && images.length > 0 && images.length < expectedFrames;
  const imagesComplete = images.length > 0 && (expectedFrames === 0 || images.length >= expectedFrames);

  // Assembling with frames missing yields a video with beats that have nothing to show, so the
  // video stage waits for a complete set rather than merely a "done" flag.
  const videoReady = imagesComplete && st.voice.done;

  const runStage = (s: any, p?: string, force?: boolean, music?: boolean, provider?: ImageProvider) =>
    !busy && stage.mutate({ stage: s, privacy: p, force, music, provider });

  // Quota state per image backend. The two do not draw alike — a set finished on the other one
  // changes art style mid-video — so each backend gets its own button and the spent one is
  // disabled rather than silently substituted.
  const gflowHealth = providerHealth?.find((p) => p.provider === "gflow");
  const codexHealth = providerHealth?.find((p) => p.provider === "codex");

  // Benched means "ran dry, and the cooldown hasn't lapsed" — NOT "cannot be used". Signing Flow
  // into another account resets the real limit, and nothing reports that, so these stay clickable
  // and say what pressing them will do.
  const benchLabel = (h?: ProviderHealth) =>
    h?.benched
      ? `Ran out of quota — about ${Math.max(1, Math.round(h.retryInMinutes ?? 0))} min until it's ` +
        `expected back. Press it anyway if you've switched to another account.`
      : null;

  // What the next assembly will do about background music: your unsaved pick, else what this run
  // chose last time, else the channel's own setting.
  const channel = channels?.find((c) => c.id === m.channel);
  const channelMusic = channel?.music ?? true;
  const musicOn = musicChoice ?? st.video.music ?? channelMusic;

  // Which ElevenLabs model the next voiceover uses: your unsaved pick, else the one this video kept
  // from last time, else "" — the channel's model, which the first option names.
  const voiceModel = voicePick ?? st.voice.modelChoice ?? "";
  const channelModel = channelVoiceModel(channel, settings);

  // Which stage is *actually* running right now. For an auto-run (one "auto" job that
  // walks script → voice → images → video) we derive it from the first incomplete stage,
  // so the correct card lights up live instead of "script" staying lit the whole time.
  // The job query has not resolved on the first render after opening a run the server already
  // reports as busy, so this has to read through to the server's answer rather than assert that
  // the local job exists — asserting it crashed the whole view to a blank page whenever a run was
  // opened while a stage it had not started was running.
  const runningStage = job?.stage ?? runQ.data?.activeJob?.stage ?? null;
  const activeStage: string | null = !busy
    ? null
    : runningStage === "auto"
      ? (!st.script.done ? "script"
        : !st.voice.done ? "voice"
        : !st.images.done ? "images"
        : !st.video.done ? "video"
        : null)
      : runningStage;

  return (
    <div className="mx-auto max-w-3xl">

      {/* ── Back to the grid ────────────────────────────────────────────── */}
      <div className="mb-4 flex items-center">
        <button
          onClick={() => useUi.getState().select(null)}
          className="flex items-center gap-1.5 text-xs font-medium text-white/40 transition-colors hover:text-white/80"
        >
          <ArrowLeft className="size-3.5" /> All videos
        </button>
        <div className="flex-1" />
        {hasVideo && !m.archive && (
          <Button
            size="sm"
            variant="ghost"
            disabled={reveal.isPending}
            title={reveal.isError
              ? `Couldn't open the folder: ${(reveal.error as Error).message}`
              : "Open the folder with final.mp4 selected"}
            onClick={() => reveal.mutate(runId)}
          >
            <FolderOpen className="size-3.5" />
            {reveal.isError ? "Couldn't open folder" : "Show in folder"}
          </Button>
        )}
        {!m.archive && (
          <Button
            size="sm"
            variant="ghost"
            disabled={busy || !driveReady}
            title={busy
              ? "Stop the running job first"
              : !driveReady
                ? "Set up Google Drive in Settings → Storage first"
                : "Move this video's files to Google Drive, freeing the space on this PC"}
            onClick={() => setMoving(true)}
          >
            <CloudUpload className="size-3.5" /> Move to Google Drive
          </Button>
        )}
        <Button
          size="sm"
          variant="ghost"
          disabled={busy}
          title={busy ? "Stop the running job first" : "Delete this video and all its files"}
          onClick={() => setConfirmDelete(true)}
          className="hover:text-red-400"
        >
          <Trash2 className="size-3.5" /> Delete
        </Button>
      </div>

      {/* ── Run header ──────────────────────────────────────────────────── */}
      <div className="mb-5 flex items-start gap-3">
        <div className="flex-1">
          {/* The topic as typed is the title — see TitleForPublish. Runs created before that was
              pinned still carry a generated publish.title, so the topic leads here. */}
          <h2 className="text-xl font-semibold leading-tight">{m.topic || m.publish.title}</h2>
          <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-xs text-white/40">
            <Badge tone="channel">{m.channel}</Badge>
            <span className="flex items-center gap-1">
              <Clock className="size-3" />
              {new Date(m.createdUtc).toLocaleString()}
            </span>
            {/* Planned publish date — drives the Calendar, triggers nothing on its own. */}
            <span className="flex items-center gap-1.5" title="Plan only — nothing uploads on a timer.">
              <CalendarDays className="size-3" /> Planned
            <input
              type="date"
              className="rounded-md border border-white/[0.08] bg-white/[0.04] px-1.5 py-0.5 text-[11px] text-white/80 outline-none focus:border-amber-500/50 [color-scheme:dark]"
              value={m.publish.scheduledUtc
                ? new Date(m.publish.scheduledUtc).toLocaleDateString("en-CA")
                : ""}
              onChange={(e) => schedule.mutate({
                runId,
                // Noon local keeps the calendar day stable through the UTC round-trip.
                scheduledUtc: e.target.value
                  ? new Date(`${e.target.value}T12:00:00`).toISOString()
                  : null,
              })}
            />
            {m.publish.scheduledUtc && (
              <Button size="xs" variant="ghost"
                onClick={() => schedule.mutate({ runId, scheduledUtc: null })}>
                Clear
              </Button>
            )}
            </span>
            <code className="rounded bg-white/[0.05] px-1.5 py-0.5 font-mono text-[10px] text-white/30">
              {m.runId}
            </code>
          </div>
        </div>
        {busy && (
          <div className="flex shrink-0 items-center gap-2">
            <Badge tone="run"><Spinner /> {activeStage ?? job?.stage}…</Badge>
            {/* Also in the log overlay, but that can be dismissed — this stays put. */}
            <Button
              size="xs" variant="danger"
              loading={cancel.isPending}
              title="Stop the running job"
              onClick={() => jobId && cancel.mutate(jobId)}
            >
              <Square className="size-2.5 fill-current" /> Stop
            </Button>
          </div>
        )}
      </div>

      {driveError && (
        <div className="mb-3 rounded-lg border border-red-500/25 bg-red-500/[0.06] px-3 py-2 text-xs text-red-200/80">
          {driveError}
        </div>
      )}

      {/* ── In Google Drive ─────────────────────────────────────────────── */}
      {m.archive && (
        <div className="rounded-xl border border-sky-500/20 bg-sky-500/[0.04] p-4">
          <div className="flex items-start gap-3">
            <Cloud className="mt-0.5 size-5 shrink-0 text-sky-300" />
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-white/85">In Google Drive</p>
              <p className="mt-1 text-xs leading-relaxed text-white/45">
                The script, voiceover, frames and video were moved off this PC on{" "}
                {new Date(m.archive.movedUtc).toLocaleString()} ({m.archive.files} files,{" "}
                {Math.round(m.archive.bytes / 1048576)} MB). Bring it back to edit, re-assemble or publish it.
              </p>
              <code className="mt-2 block break-all rounded bg-white/[0.05] px-2 py-1 font-mono text-[10px] text-white/40">
                {m.archive.remote || m.archive.location}
              </code>
              <div className="mt-3 flex flex-wrap gap-2">
                <Button
                  size="sm"
                  disabled={reveal.isPending || !m.archive.location}
                  title={m.archive.location
                    ? "Open its folder in File Explorer"
                    : "Set your Google Drive folder in Settings → Storage to open it here"}
                  onClick={() => reveal.mutate(runId)}
                >
                  <FolderOpen className="size-3.5" /> Open in Google Drive
                </Button>
                <Button
                  size="sm"
                  variant="primary"
                  disabled={busy}
                  loading={drive.isPending}
                  onClick={() => drive.mutate("restore")}
                >
                  <CloudDownload className="size-3.5" /> Bring back to this PC
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* ── Stage pipeline ──────────────────────────────────────────────── */}
      {/* Hidden rather than unmounted while the files are in Drive: none of it has anything to show. */}
      <div className={cn("space-y-2.5", m.archive && "hidden")}>

        {/* 1 — Script */}
        <StageCard
          stageKey="script" num={1} title="Script — Claude Opus 4.8"
          subtitle={script ? `${script.beats.length} beats · ${script.title}` : undefined}
          defaultCollapsed={st.script.done}
          done={st.script.done} running={activeStage === "script"}
          actions={
            <Button
              disabled={busy}
              title={busy ? "A job is already running" : ""}
              onClick={() => runStage("script")}
            >
              {script ? "Regenerate script" : "Retry script"}
            </Button>
          }
          status={st.script.done && (
            sa
              ? <Badge tone="ok">approved</Badge>
              : <Button size="xs" variant="primary" disabled={busy}
                  onClick={(e) => { e.stopPropagation(); approve.mutate("script"); }}>
                  Approve ▶
                </Button>
          )}
        >
          {!script && activeStage !== "script" ? (
            // A spinner here regardless of state used to make a dead run look identical to a
            // working one — which is exactly how a failed script stage went unnoticed.
            <div className="space-y-2.5">
              <div className="rounded-lg border border-red-500/25 bg-red-500/[0.06] px-3 py-2.5 text-xs leading-relaxed text-red-200/80">
                <p className="font-semibold">No script — generation didn't finish.</p>
                <p className="mt-1 text-red-200/60">
                  Nothing is running for this video. The usual cause is the <code>claude</code> CLI
                  failing at launch; check the log for the line after <code>exec claude</code>.
                  Fix the cause, then retry below — no need to recreate the video.
                </p>
              </div>
            </div>
          ) : script ? (
            <div className="space-y-2.5">
              <div>
                <p className="font-semibold text-white/90">{script.title}</p>
                <p className="mt-1 text-sm text-white/40">{script.description}</p>
              </div>
              <div>{script.tags?.map((t) => <Tag key={t}>{t}</Tag>)}</div>
              <div className="rounded-lg border border-white/[0.06] bg-white/[0.02]">
                {script.beats.map((b, i) => (
                  <div key={b.index} className={cn(
                    "px-3 py-2.5 text-sm",
                    i !== 0 && "border-t border-white/[0.04]"
                  )}>
                    <span className="mr-2 text-xs font-bold tabular-nums text-amber-500/80">
                      {String(b.index).padStart(2, "0")}
                    </span>
                    <span className="text-white/75">{b.narration}</span>
                    {b.imagePrompt && (
                      <p className="mt-0.5 text-[11px] italic text-white/25">{b.imagePrompt}</p>
                    )}
                  </div>
                ))}
              </div>
            </div>
          ) : (
            <p className="flex items-center gap-2 text-sm text-white/30">
              <Spinner /> Generating…
            </p>
          )}
        </StageCard>

        {/* 2 — Voice */}
        <StageCard
          stageKey="voice" num={2} done={st.voice.done}
          running={activeStage === "voice"}
          defaultCollapsed={st.voice.done}
          title="Voiceover — ElevenLabs"
          subtitle={st.voice.seconds
            ? `${Math.floor(st.voice.seconds / 60)}:${String(Math.round(st.voice.seconds % 60)).padStart(2, "0")} of narration` +
              (st.voice.model ? ` · ${voiceModelLabel(st.voice.model)}` : "")
            : undefined}
          actions={
            <>
              <Button
                disabled={busy || !st.script.done}
                // "default" rather than nothing: nothing would keep a model this video picked before.
                onClick={() => !busy && stage.mutate({ stage: "voice", model: voiceModel || "default" })}
              >
                {st.voice.done ? "Re-render voiceover" : "Generate voiceover"}
              </Button>

              {/* Chooses the model for the button above, for this video only — the channel keeps
                  its own. Nothing is voiced until you press the button. */}
              <select
                aria-label="Voice model"
                title="ElevenLabs model for this video. The channel's own model is unchanged."
                className={cn(selectCls, "h-8 py-0 text-xs")}
                value={voiceModel}
                disabled={busy}
                onChange={(e) => setVoicePick(e.target.value)}
              >
                <option value="">Channel's model — {voiceModelLabel(channelModel)}</option>
                {voiceModelOptions(voiceModel).map((vm) => (
                  <option key={vm.id} value={vm.id}>{vm.label} — {vm.note}</option>
                ))}
              </select>
            </>
          }
        >
          {hasAudio && (
            <audio
              // Keyed so React swaps the element outright when the file is re-rendered; changing
              // src alone can leave the old buffered media in place.
              key={st.voice.completedUtc ?? "audio"}
              controls
              className="w-full"
              src={api.audioUrl(runId, st.voice.completedUtc)}
            />
          )}
        </StageCard>

        {/* 3 — Images */}
        <StageCard
          stageKey="images" num={3}
          // NOT st.images.done: the stage marks itself done even when it only produced some of
          // the frames, so trusting that flag puts a green tick on an unfinished set.
          done={imagesComplete}
          running={activeStage === "images"}
          locked={!sa}
          defaultCollapsed={imagesComplete}
          title={`Images — ${imageProvider}`}
          subtitle={images.length
            ? `${images.length}${expectedFrames ? ` of ${expectedFrames}` : ""} frames`
            : undefined}
          status={imagesPartial && <Badge tone="warn">{images.length}/{expectedFrames} — incomplete</Badge>}
          actions={
            <>
              {/* One button per backend. force stays FALSE on both: these are the buttons you
                  press after a quota reset, and clearing first would throw away the frames you
                  waited for. */}
              <Button
                variant={imagesPartial && !gflowHealth?.benched ? "primary" : "secondary"}
                disabled={busy || !sa}
                title={!sa
                  ? "Approve the script first"
                  : benchLabel(gflowHealth)
                    ?? "Generates the missing beats on gflow — existing frames are kept"}
                onClick={() => runStage("images", undefined, false, undefined, "gflow")}
              >
                {gflowHealth?.benched
                  ? "Try gflow anyway"
                  : images.length === 0
                    ? "Generate on gflow"
                    : imagesPartial
                      ? `Continue on gflow — ${expectedFrames - images.length} left`
                      : "Fill any gaps — gflow"}
              </Button>

              <Button
                variant="secondary"
                disabled={busy || !sa}
                title={!sa
                  ? "Approve the script first"
                  : (codexHealth?.benched ? benchLabel(codexHealth) + " " : "") +
                    "codex draws more detail than gflow, so a set split between the two changes " +
                    "style mid-video."}
                onClick={() => runStage("images", undefined, false, undefined, "codex")}
              >
                {images.length === 0 ? "Generate on codex" : "Continue on codex"}
              </Button>

              {images.length > 0 && (
                <Button
                  variant="danger"
                  disabled={busy || !sa}
                  title="Deletes all existing frames first, then regenerates every beat from scratch"
                  onClick={() => {
                    if (confirm(
                      `Delete all ${images.length} existing frame(s) and regenerate from scratch?\n\n` +
                      `They cost image quota to make, and this cannot be undone. ` +
                      `To only fill missing beats, use the other button instead.`)) {
                      runStage("images", undefined, true);
                    }
                  }}
                >
                  Regenerate all
                </Button>
              )}
            </>
          }
        >
          {imagesPartial && (
            <div className="rounded-lg border border-amber-500/25 bg-amber-500/[0.06] px-3 py-2.5 text-xs leading-relaxed text-amber-200/80">
              <span className="font-semibold">
                {images.length} of {expectedFrames} frames done.
              </span>{" "}
              The stage paused — usually the daily image quota. Continuing generates only the
              missing beats and keeps what's already here.{" "}
              {gflowHealth?.benched
                ? <>gflow ran out of quota about{" "}
                    {Math.max(1, Math.round(gflowHealth.retryInMinutes ?? 0))} min short of its
                    expected reset. <strong>If you've signed Flow into another account, press
                    Try gflow anyway</strong> — a new account has its own limit, which nothing here
                    can see. Otherwise wait, or continue on codex, which draws far more detail and
                    will visibly change the art style from this beat onward.</>
                : <>Pick the backend that made the frames you already have, so the style stays
                    consistent.</>}{" "}
              Don't press <strong>Regenerate all</strong> unless you want to start over and spend
              the quota again.
            </div>
          )}
          {images.length > 0 && (
            <div className="grid grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-2">
              {/* Capped by default: rendering 90+ full-size PNGs at once made the panel crawl. */}
              {(showAllImages ? images : images.slice(0, IMAGE_PREVIEW_COUNT)).map((f) => (
                <figure key={f} className="group relative m-0">
                  <img
                    loading="lazy"
                    className="w-full rounded-lg border border-white/[0.07]"
                    src={api.imageUrl(runId, f)}
                  />
                  {armedFrame === f ? (
                    <div className="absolute inset-x-1 top-1 flex gap-1">
                      <button
                        className="flex-1 rounded-md bg-red-600/90 px-1.5 py-1 text-[10px] font-medium text-white hover:bg-red-600 disabled:opacity-50"
                        disabled={deleteImage.isPending}
                        onClick={() => deleteImage.mutate(
                          { runId, name: f },
                          { onSettled: () => setArmedFrame(null) },
                        )}
                      >
                        Delete
                      </button>
                      <button
                        className="rounded-md bg-black/80 px-1.5 py-1 text-[10px] text-white/70 hover:text-white"
                        onClick={() => setArmedFrame(null)}
                      >
                        Cancel
                      </button>
                    </div>
                  ) : (
                    <button
                      title="Delete this frame — the next images run regenerates it"
                      className="absolute right-1 top-1 hidden rounded-md bg-black/70 p-1.5 text-white/60 hover:text-white group-hover:block"
                      onClick={() => setArmedFrame(f)}
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  )}
                  <figcaption className="mt-1 break-all text-[10px] text-white/25">{f}</figcaption>
                </figure>
              ))}
            </div>
          )}
          {images.length > IMAGE_PREVIEW_COUNT && (
            <Button size="sm" variant="subtle" onClick={() => setShowAllImages((v) => !v)}>
              {showAllImages
                ? `Show fewer`
                : `Show all ${images.length} frames`}
            </Button>
          )}
        </StageCard>

        {/* 4 — Video */}
        <StageCard
          stageKey="video" num={4} title="Video — FFmpeg"
          defaultCollapsed={st.video.done}
          subtitle={st.video.musicTracks?.length
            ? `music: ${st.video.musicTracks.join(", ")}`
            : st.video.done
              ? "no background music"
              : undefined}
          done={st.video.done}
          running={activeStage === "video"}
          locked={!videoReady}
          status={st.video.done && (
            va
              ? <Badge tone="ok">approved</Badge>
              : <Button size="xs" variant="primary" disabled={busy}
                  onClick={(e) => { e.stopPropagation(); approve.mutate("video"); }}>
                  Approve ▶
                </Button>
          )}
          actions={
            <>
              <Button
                disabled={busy || !videoReady}
                title={videoReady
                  ? ""
                  : imagesPartial
                    ? `Only ${images.length} of ${expectedFrames} frames exist — finish the images stage first`
                    : "Need voice + images first"}
                onClick={() => runStage("video", undefined, undefined, musicOn)}
              >
                {/* The music choice is named ON the button. It used to read just "Assemble video"
                    while a separate toggle silently decided the outcome, so it was possible to
                    cut the wrong version without ever noticing there was a choice. */}
                {st.video.done ? "Re-assemble" : "Assemble"}{musicOn ? " with music" : " voice-only"}
              </Button>

              {/* Chooses what the button above will do. The mp4 is only re-cut when you press
                  Re-assemble, so flipping this on its own changes nothing yet. */}
              <button
                type="button"
                role="switch"
                aria-checked={musicOn}
                disabled={busy}
                onClick={() => setMusicChoice(!musicOn)}
                title={musicOn
                  ? "Switch off to assemble a voice-only video — the music credit leaves the description too"
                  : "Switch on to put background music back"}
                className={cn(
                  "flex items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40",
                  musicOn
                    ? "border-white/[0.08] bg-white/[0.02] text-white/55 hover:border-white/[0.16]"
                    : "border-amber-500/30 bg-amber-500/[0.08] text-amber-400",
                )}
              >
                {musicOn ? <Music className="size-3.5" /> : <VolumeX className="size-3.5" />}
                {musicOn ? "With background music" : "No background music"}
              </button>
            </>
          }
        >
          {/* Only worth saying while the mp4 on disk still disagrees with the toggle. */}
          {!musicOn && (st.video.musicTracks?.length ?? 0) > 0 && (
            <div className="rounded-lg border border-amber-500/25 bg-amber-500/[0.06] px-3 py-2.5 text-xs leading-relaxed text-amber-200/80">
              <span className="font-semibold">This video still has music in it.</span>{" "}
              Press <strong>Re-assemble video</strong> to cut a voice-only version — that also
              drops the <code>Music:</code> credit from the description below, since there's
              nothing left to credit.
            </div>
          )}
          {hasVideo && (
            <video
              key={st.video.completedUtc ?? "video"}
              controls
              className="w-full rounded-lg"
              src={api.videoUrl(runId, st.video.completedUtc)}
            />
          )}
          {st.video.musicTracks && st.video.musicTracks.length > 0 && (
            <div className="mt-2">
              <div className="mb-1.5"><SectionLabel>Background music</SectionLabel></div>
              <div className="flex flex-wrap gap-1.5">
                {st.video.musicTracks.map((t, i) => (
                  <span key={t} className="flex items-center gap-1 rounded-full bg-white/[0.06] px-2.5 py-0.5 text-xs text-white/60">
                    <span className="text-[10px] text-white/25">{i + 1}</span>
                    {t}
                  </span>
                ))}
              </div>
            </div>
          )}
        </StageCard>

        {/* Publish details */}
        {st.video.done && (
          <StageCard
            stageKey="publish" num="★" title="Title, description & thumbnail"
            subtitle="What goes out with every upload"
            defaultCollapsed
            done={st.upload.done}
          >
            <div className="grid grid-cols-1 gap-4 md:grid-cols-[240px_1fr]">
              <div>
                <div className="mb-2"><SectionLabel>Thumbnail</SectionLabel></div>
                {hasThumbnail ? (
                  <img className="w-full rounded-lg border border-white/[0.07]" src={api.thumbnailUrl(runId, bust)} />
                ) : (
                  <div className="flex h-32 items-center justify-center rounded-lg border border-white/[0.07] text-xs text-white/25">
                    No thumbnail yet
                  </div>
                )}
                <div className="mt-2 flex gap-2">
                  <Button disabled={busy} onClick={() => runStage("thumbnail")}>
                    {hasThumbnail ? "Regenerate" : "Generate"} thumbnail
                  </Button>
                  {hasThumbnail && (
                    <a href={api.thumbnailUrl(runId)} download="thumbnail.png">
                      <Button variant="ghost" size="md"><Download className="size-3.5" /> Download</Button>
                    </a>
                  )}
                </div>
              </div>
              <div className="space-y-3">
                <div>
                  <div className="mb-2"><SectionLabel>Video title</SectionLabel></div>
                  <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-sm text-white/80">
                    {m.topic || m.publish.title || script?.title}
                  </div>
                </div>
                <div>
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <SectionLabel>Description</SectionLabel>
                    {/* Rewrites ONLY this text — the mp4, its frames and the voiceover are
                        untouched. It's how a channel's description format reaches a video that
                        was written before the format existed. */}
                    <Button
                      size="xs" variant="ghost" disabled={busy}
                      title="Rewrite the description to the channel's format. The video, frames and voiceover are not touched."
                      onClick={() => runStage("description")}
                    >
                      Rewrite description
                    </Button>
                  </div>
                  <div className="max-h-48 overflow-auto whitespace-pre-wrap rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs leading-relaxed text-white/50">
                    {m.publish.description || script?.description}
                  </div>
                </div>
                <div>
                  <div className="mb-2"><SectionLabel>Tags</SectionLabel></div>
                  <div>{(m.publish.tags?.length ? m.publish.tags : script?.tags ?? []).map((t) => <Tag key={t}>{t}</Tag>)}</div>
                </div>
              </div>
            </div>
          </StageCard>
        )}

        {/* 5 — Publish: every destination in one card */}
        {st.video.done && (
          <PublishCard
            st={st}
            approved={va}
            busy={busy}
            activeStage={activeStage}
            privacy={privacy}
            onPrivacy={setPrivacy}
            onPublish={(s, p) => runStage(s, p)}
          />
        )}
      </div>

      {/* ── Job log overlay ──────────────────────────────────────────────── */}
      <JobLog
        job={job}
        title={`${activeStage ?? job?.stage ?? ""} · ${m.channel}`}
        onStop={jobId && busy ? () => cancel.mutate(jobId) : undefined}
        onClose={() => useUi.getState().clearJob(runId)}
      />

      <DeleteVideoDialog
        target={confirmDelete ? m : null}
        onClose={() => setConfirmDelete(false)}
      />
      <MoveToDriveDialog target={moving ? m : null} onClose={() => setMoving(false)} />
    </div>
  );
}

/**
 * Every publish destination in one card. They used to be three full cards, each repeating a
 * disabled button and a paragraph until the video was approved; the gate is said once here.
 * Destinations are optional and order-independent, and a `skipReason` from the last attempt is
 * shown in place of an error because "not configured" is the normal state, not a failure.
 */
function PublishCard({
  st, approved, busy, activeStage, privacy, onPrivacy, onPublish,
}: {
  st: Manifest["stages"]; approved: boolean; busy: boolean; activeStage: string | null;
  privacy: string; onPrivacy: (privacy: string) => void;
  onPublish: (stage: StageName, privacy?: string) => void;
}) {
  const where = [
    st.upload.done && "YouTube", st.facebook.done && "Facebook", st.instagram.done && "Instagram",
  ].filter(Boolean).join(", ");
  const running = ["upload", "facebook", "instagram", "publish-all"].includes(activeStage ?? "");
  const blocked = !approved ? "Approve the video first" : busy ? "A job is already running" : "";

  return (
    <StageCard
      stageKey="publish-targets" num={5} title="Publish"
      subtitle={where ? `Published to ${where}` : approved ? "Not published yet" : "Approve the video to publish it"}
      done={!!where}
      running={running}
      locked={!approved}
      defaultCollapsed={!!where}
      actions={
        <Button
          disabled={!!blocked}
          title={blocked || "Publish to every destination above"}
          onClick={() => onPublish("publish-all", privacy)}
        >
          <Rocket className="size-3.5" /> Publish everywhere
        </Button>
      }
    >
      <div className="divide-y divide-white/[0.05] overflow-hidden rounded-lg border border-white/[0.06]">
        <PublishRow
          icon={<MonitorPlay />} name="YouTube" status={st.upload}
          link={st.upload.videoId
            ? { href: `https://youtu.be/${st.upload.videoId}`, label: `youtu.be/${st.upload.videoId}` }
            : undefined}
          hint="Optional while testing"
          blocked={blocked}
          action={st.upload.done ? "Upload again" : "Upload"}
          onAction={() => onPublish("upload", privacy)}
          control={
            <select
              className={cn(selectCls, "h-7 w-24 py-0 text-xs")}
              value={privacy}
              onChange={(e) => onPrivacy(e.target.value)}
            >
              <option>private</option>
              <option>unlisted</option>
              <option>public</option>
            </select>
          }
        />
        <PublishRow
          icon={<ThumbsUp />} name="Facebook Page" status={st.facebook}
          link={st.facebook.url ? { href: st.facebook.url, label: "View post" } : undefined}
          hint="Uploads the mp4 directly"
          blocked={blocked}
          action={st.facebook.done ? "Publish again" : "Publish"}
          onAction={() => onPublish("facebook")}
        />
        <PublishRow
          icon={<Camera />} name="Instagram Reel" status={st.instagram}
          link={st.instagram.url ? { href: st.instagram.url, label: "View post" } : undefined}
          hint="Needs public video hosting, which isn't set up yet"
          blocked={blocked}
          action={st.instagram.done ? "Publish again" : "Publish"}
          onAction={() => onPublish("instagram")}
        />
      </div>
    </StageCard>
  );
}

function PublishRow({
  icon, name, status, link, hint, blocked, action, onAction, control,
}: {
  icon: ReactNode; name: string; status: StageStatus; link?: { href: string; label: string };
  hint: string; blocked: string; action: string; onAction: () => void; control?: ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2.5">
      <span className="grid size-7 shrink-0 place-items-center rounded-md bg-white/[0.04] text-white/50 [&_svg]:size-3.5">
        {icon}
      </span>
      <div className="min-w-0 flex-1">
        <div className="text-xs font-medium text-white/80">{name}</div>
        <div className="truncate text-[11px] text-white/35" title={status.skipReason}>
          {status.done
            ? link
              ? (
                <a href={link.href} target="_blank" rel="noreferrer"
                  className="inline-flex items-center gap-1 text-emerald-400 hover:underline">
                  {link.label} <ExternalLink className="size-2.5" />
                </a>
              )
              : <span className="text-emerald-400">Published</span>
            : status.skipReason
              ? <span className="text-amber-300/80">Last attempt did nothing: {status.skipReason}</span>
              : hint}
        </div>
      </div>
      {control}
      <Button size="sm" disabled={!!blocked} title={blocked} onClick={onAction}>{action}</Button>
    </div>
  );
}
