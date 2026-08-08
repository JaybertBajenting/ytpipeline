import { useState, type ReactNode } from "react";
import { ArrowLeft, Camera, Clock, Download, ExternalLink, ThumbsUp, Trash2 } from "lucide-react";
import { api, type StageStatus } from "@/lib/api";
import { useApprove, useCancelJob, useJob, useRun, useRunStage } from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { Badge, Button, SectionLabel, Spinner, Tag, selectCls } from "./ui";
import { StageCard } from "./StageCard";
import { JobLog } from "./JobLog";
import { DeleteVideoDialog } from "./DeleteVideoDialog";
import { cn } from "@/lib/utils";

export function RunDetail({ runId }: { runId: string }) {
  const jobId = useUi((s) => s.jobs[runId]) ?? null;
  const job = useJob(jobId, runId);
  const busy = job?.status === "running";
  const runQ = useRun(runId, busy);           // poll live while a job is running
  const stage = useRunStage(runId);
  const approve = useApprove(runId);
  const cancel = useCancelJob();
  const [privacy, setPrivacy] = useState("private");
  const [confirmDelete, setConfirmDelete] = useState(false);

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
  const videoReady = st.images.done && st.voice.done;

  const runStage = (s: any, p?: string, force?: boolean) =>
    !busy && stage.mutate({ stage: s, privacy: p, force });

  // Which stage is *actually* running right now. For an auto-run (one "auto" job that
  // walks script → voice → images → video) we derive it from the first incomplete stage,
  // so the correct card lights up live instead of "script" staying lit the whole time.
  const activeStage: string | null = !busy
    ? null
    : job!.stage === "auto"
      ? (!st.script.done ? "script"
        : !st.voice.done ? "voice"
        : !st.images.done ? "images"
        : !st.video.done ? "video"
        : null)
      : job!.stage;

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
          <h2 className="text-xl font-semibold leading-tight">{m.publish.title || m.topic}</h2>
          <div className="mt-1.5 flex flex-wrap items-center gap-2 text-xs text-white/40">
            <Badge tone="channel">{m.channel}</Badge>
            <span className="flex items-center gap-1">
              <Clock className="size-3" />
              {new Date(m.createdUtc).toLocaleString()}
            </span>
            <code className="rounded bg-white/[0.05] px-1.5 py-0.5 font-mono text-[10px] text-white/30">
              {m.runId}
            </code>
          </div>
        </div>
        {busy && <Badge tone="run"><Spinner /> {activeStage ?? job?.stage}…</Badge>}
      </div>

      {/* ── Stage pipeline ──────────────────────────────────────────────── */}
      <div className="space-y-2.5">

        {/* 1 — Script */}
        <StageCard
          stageKey="script" num={1} title="Script — Claude Opus 4.8"
          done={st.script.done} running={activeStage === "script"}
          status={st.script.done && (
            sa
              ? <Badge tone="ok">approved</Badge>
              : <Button size="xs" variant="primary" disabled={busy}
                  onClick={(e) => { e.stopPropagation(); approve.mutate("script"); }}>
                  Approve ▶
                </Button>
          )}
        >
          {script ? (
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
          title={`Voiceover — ElevenLabs${st.voice.seconds ? ` · ${st.voice.seconds}s` : ""}`}
          actions={
            <Button disabled={busy || !st.script.done} onClick={() => runStage("voice")}>
              {st.voice.done ? "Re-render voiceover" : "Generate voiceover"}
            </Button>
          }
        >
          {hasAudio && <audio controls className="w-full" src={api.audioUrl(runId)} />}
        </StageCard>

        {/* 3 — Images */}
        <StageCard
          stageKey="images" num={3} done={st.images.done}
          running={activeStage === "images"}
          locked={!sa}
          title={`Images — Codex${st.images.count ? ` · ${st.images.count} frames` : ""}`}
          actions={
            <Button
              disabled={busy || !sa}
              title={sa ? (st.images.done ? "Clears existing frames and regenerates all" : "") : "Approve the script first"}
              onClick={() => runStage("images", undefined, st.images.done)}
            >
              {st.images.done ? "Regenerate images" : "Generate images"}
            </Button>
          }
        >
          {images.length > 0 && (
            <div className="grid grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-2">
              {images.map((f) => (
                <figure key={f} className="m-0">
                  <img
                    loading="lazy"
                    className="w-full rounded-lg border border-white/[0.07]"
                    src={api.imageUrl(runId, f)}
                  />
                  <figcaption className="mt-1 break-all text-[10px] text-white/25">{f}</figcaption>
                </figure>
              ))}
            </div>
          )}
        </StageCard>

        {/* 4 — Video */}
        <StageCard
          stageKey="video" num={4} title="Video — FFmpeg" done={st.video.done}
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
            <Button
              disabled={busy || !videoReady}
              title={videoReady ? "" : "Need voice + images first"}
              onClick={() => runStage("video")}
            >
              {st.video.done ? "Re-assemble video" : "Assemble video"}
            </Button>
          }
        >
          {hasVideo && <video controls className="w-full rounded-lg" src={api.videoUrl(runId)} />}
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
          <StageCard stageKey="publish" num="★" title="Publish details" done={st.upload.done}>
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
                    {m.publish.title || script?.title}
                  </div>
                </div>
                <div>
                  <div className="mb-2"><SectionLabel>Description</SectionLabel></div>
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

        {/* 5 — Upload */}
        <StageCard
          stageKey="upload" num={5} title="Upload — YouTube"
          done={st.upload.done} locked={!va}
          running={activeStage === "upload"}
          status={st.upload.done && st.upload.videoId && (
            <a
              href={`https://youtu.be/${st.upload.videoId}`}
              target="_blank" rel="noreferrer"
              onClick={(e) => e.stopPropagation()}
              className="flex items-center gap-1 text-[11px] text-amber-400 hover:underline"
            >
              youtu.be/{st.upload.videoId} <ExternalLink className="size-2.5" />
            </a>
          )}
          actions={
            <>
              <select className={cn(selectCls, "w-28")} value={privacy} onChange={(e) => setPrivacy(e.target.value)}>
                <option>private</option>
                <option>unlisted</option>
                <option>public</option>
              </select>
              <Button
                disabled={busy || !va}
                title={va ? "" : "Approve the video first"}
                onClick={() => runStage("upload", privacy)}
              >
                Upload to YouTube
              </Button>
              <span className="text-xs text-white/25">Optional — skip while testing.</span>
            </>
          }
        />

        {/* Extra distribution targets — independent of the YouTube upload. */}
        <PublishTarget
          stageKey="facebook" label="Facebook Page" icon={<ThumbsUp className="size-3.5" />}
          status={st.facebook} locked={!va} busy={busy}
          running={activeStage === "facebook"}
          onPublish={() => runStage("facebook")}
          note="Uploads the mp4 directly — no hosting needed."
        />

        <PublishTarget
          stageKey="instagram" label="Instagram Reel" icon={<Camera className="size-3.5" />}
          status={st.instagram} locked={!va} busy={busy}
          running={activeStage === "instagram"}
          onPublish={() => runStage("instagram")}
          note="Needs the channel's public video URL — Meta fetches the file, so this stays inert until hosting is wired up."
        />
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
    </div>
  );
}

/**
 * One extra distribution target. Not numbered like the pipeline stages — these are optional and
 * order-independent, and a `skipReason` from the last attempt is shown in place of an error
 * because "not configured" is the normal state here, not a failure.
 */
function PublishTarget({
  stageKey, label, icon, status, locked, busy, running, onPublish, note,
}: {
  stageKey: string; label: string; icon: ReactNode; status: StageStatus;
  locked?: boolean; busy?: boolean; running?: boolean; onPublish: () => void; note: string;
}) {
  return (
    <StageCard
      stageKey={stageKey}
      num={icon}
      title={label}
      done={status.done}
      running={running}
      locked={locked}
      status={status.done && status.url && (
        <a
          href={status.url}
          target="_blank" rel="noreferrer"
          onClick={(e) => e.stopPropagation()}
          className="flex items-center gap-1 text-[11px] text-amber-400 hover:underline"
        >
          view post <ExternalLink className="size-2.5" />
        </a>
      )}
      actions={
        <>
          <Button
            disabled={busy || locked}
            title={locked ? "Approve the video first" : ""}
            onClick={onPublish}
          >
            {status.done ? `Publish again to ${label}` : `Publish to ${label}`}
          </Button>
          <span className="text-xs text-white/25">{note}</span>
        </>
      }
    >
      {status.skipReason && !status.done && (
        <div className="rounded-lg border border-amber-500/25 bg-amber-500/[0.06] px-3 py-2 text-xs leading-relaxed text-amber-200/80">
          <span className="font-semibold">Last attempt did nothing: </span>
          {status.skipReason}
        </div>
      )}
      {status.done && status.postId && (
        <p className="text-xs text-white/40">
          Post id <code className="rounded bg-white/[0.06] px-1 font-mono text-[10px]">{status.postId}</code>
        </p>
      )}
    </StageCard>
  );
}
