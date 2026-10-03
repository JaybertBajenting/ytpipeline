import {
  CalendarDays, Camera, CheckCircle2, ChevronRight, Cloud, CloudDownload, CloudUpload, Film,
  FolderOpen, MonitorPlay, Music, Rocket, Square, ThumbsUp, Trash2, Volume2, VolumeX,
} from "lucide-react";
import type { ReactNode } from "react";
import { api, stagesDone, type Manifest, type StageName, type StageStatus } from "@/lib/api";
import {
  useCancelJob, useDriveTransfer, useJob, useRevealVideo, useRunStage, useSettings,
} from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { Badge, Button, Card, Menu, MenuDivider, MenuItem, MenuLabel, Progress, Spinner } from "./ui";

function relative(iso: string) {
  const then = new Date(iso).getTime();
  const mins = Math.round((Date.now() - then) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(iso).toLocaleDateString();
}

/** Seconds as m:ss, the way YouTube shows a length. */
function duration(seconds: number) {
  const s = Math.round(seconds);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/**
 * One video in the grid. The preview and body open the detail view; the footer does not, so its
 * actions never fight the whole-card click target.
 *
 * The footer offers one action — whatever this video needs next — plus Google Drive and a menu for
 * the rest. It used to hold two rows of eight small buttons, most of them disabled at any moment.
 */
export function VideoCard({
  m, index, onDelete, onMoveToDrive,
}: {
  m: Manifest; index: number;
  onDelete?: (m: Manifest) => void;
  onMoveToDrive?: (m: Manifest) => void;
}) {
  const select = useUi((s) => s.select);
  const jobId = useUi((s) => s.jobs[m.runId]) ?? null;
  const running = !!jobId;
  const stage = useRunStage(m.runId);
  const cancel = useCancelJob();
  const drive = useDriveTransfer(m.runId);
  const reveal = useRevealVideo();
  const { data: settings } = useSettings();
  // Only polls when this card actually has a job (enabled: !!jobId), so a grid of 17 cards
  // isn't 17 pollers — it's however many are working.
  const job = useJob(jobId, m.runId);

  const done = stagesDone(m);
  const st = m.stages;
  const complete = st.video.done;
  // Moved to Google Drive: only the manifest and thumbnail are on this PC, so nothing that needs
  // the files is offered until it's brought back.
  const archived = !!m.archive;
  const published = st.upload.done || st.facebook.done || st.instagram.done;
  // The topic as typed is the title. Older runs still carry a generated publish.title, which is
  // only a fallback for the rare run created without a topic.
  const title = m.topic || m.publish.title;
  const openDetail = () => select(m.runId);

  // Publishing posts publicly, so it stays behind the video checkpoint — same gate the detail view uses.
  const canPublish = st.video.done && m.review.videoApproved && !running;
  const gateReason = !st.video.done
    ? "Assemble the video first"
    : !m.review.videoApproved ? "Approve the video first" : "";
  const publish = (s: StageName) => { if (canPublish) stage.mutate({ stage: s }); };
  // Re-running a finished stage. Deliberately NOT behind canPublish — redoing the voice or the cut
  // is what you do to a video that isn't approved yet.
  const regen = (s: StageName, music?: boolean) => { if (!running) stage.mutate({ stage: s, music }); };
  // Runs assembled before the music choice was recorded fall back to whether tracks came out.
  const musicOn = st.video.music ?? (st.video.musicTracks?.length ?? 0) > 0;
  // Either way of reaching Drive will do: an rclone remote, or the Drive app's folder.
  const driveReady = !!(settings?.rcloneRemote || settings?.driveArchiveFolder);

  // The one thing this video needs next.
  const next: { label: string; icon: ReactNode; title: string; primary?: boolean; onClick: () => void } =
    archived ? { label: "Bring back", icon: <CloudDownload />, title: "Copy the files back from Google Drive", onClick: () => drive.mutate("restore") }
    : !complete ? { label: "Continue", icon: <ChevronRight />, title: "Open the video to run its next stage", onClick: openDetail }
    : !m.review.videoApproved ? { label: "Review", icon: <ChevronRight />, title: "Watch the video and approve it", onClick: openDetail }
    : !published ? { label: "Publish all", icon: <Rocket />, title: "Publish to every configured target", primary: true, onClick: () => publish("publish-all") }
    : { label: "Open", icon: <ChevronRight />, title: "Open the video", onClick: openDetail };

  const facts = [
    m.channel,
    st.voice.seconds ? duration(st.voice.seconds) : null,
    st.images.count ? `${st.images.count} frames` : null,
    relative(m.createdUtc),
  ].filter(Boolean).join(" · ");

  return (
    <Card
      tone={running ? "run" : "base"}
      className="stagger group flex flex-col overflow-hidden"
      style={{ "--i": index } as React.CSSProperties}
    >
      {/* ── Preview (opens detail) ──────────────────────────────────────── */}
      <div className="relative aspect-video cursor-pointer overflow-hidden bg-black/40" onClick={openDetail}>
        {m.publish.hasThumbnail ? (
          <img
            loading="lazy"
            src={api.thumbnailUrl(m.runId)}
            alt=""
            className="size-full object-cover transition-transform duration-300 group-hover:scale-[1.03]"
          />
        ) : (
          <div className="grid size-full place-items-center">
            <Film className="size-7 text-white/[0.13]" />
          </div>
        )}

        {/* Only the status sits on the picture, and at the bottom: thumbnails put their title across
            the top, and badges up there covered it. The channel is in the line under the title. */}
        <div className="absolute bottom-2 right-2">
          {running
            ? <Badge tone="run" className="backdrop-blur-sm"><Spinner className="size-2.5" /> running</Badge>
            : archived
              ? <Badge tone="info" className="backdrop-blur-sm"><Cloud className="size-2.5" /> In Drive</Badge>
              : published
                ? <Badge tone="ok" className="backdrop-blur-sm"><CheckCircle2 className="size-2.5" /> published</Badge>
                : complete
                  ? <Badge tone="ok" className="backdrop-blur-sm"><CheckCircle2 className="size-2.5" /> ready</Badge>
                  : <Badge tone="muted" className="backdrop-blur-sm">{done}/5</Badge>}
        </div>

        <div className="absolute inset-x-0 bottom-0">
          <Progress value={done} total={5} indeterminate={running} className="h-[3px] rounded-none" />
        </div>
      </div>

      {/* ── Title and one line of facts (opens detail) ──────────────────── */}
      <div className="flex flex-1 cursor-pointer flex-col gap-1.5 px-3.5 pb-3 pt-3" onClick={openDetail}>
        {/* The second line used to carry the topic when a generated title displaced it. The title
            IS the topic now, so it would just print the same text twice. */}
        <h3 className="clamp-2 text-[13px] font-semibold leading-snug text-white/90">{title}</h3>
        <div className="mt-auto flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] tabular-nums text-white/35">
          <span>{facts}</span>
          {m.publish.scheduledUtc && (
            <span className="flex items-center gap-1 text-sky-300/70">
              <CalendarDays className="size-3" />
              {new Date(m.publish.scheduledUtc).toLocaleDateString(undefined, { month: "short", day: "numeric" })}
            </span>
          )}
        </div>
      </div>

      {/* ── Footer (does NOT open detail) ───────────────────────────────── */}
      {running ? (
        // While a job runs, every action would be refused anyway, so the footer becomes the way to
        // stop it — a job started from the grid can be cancelled from the grid.
        <div className="flex items-center gap-2 border-t border-amber-500/25 bg-amber-500/[0.06] px-2 py-1.5">
          <Spinner className="size-3 shrink-0 text-amber-400" />
          <span className="min-w-0 flex-1 truncate text-[10px] font-medium text-amber-400">
            {job?.stage ? `${job.stage}…` : "working…"}
          </span>
          <Button
            size="xs"
            variant="danger"
            loading={cancel.isPending}
            title="Stop this job"
            onClick={(e) => { e.stopPropagation(); if (jobId) cancel.mutate(jobId); }}
          >
            <Square className="size-2.5 fill-current" /> Stop
          </Button>
        </div>
      ) : (
        <div className="flex items-center gap-1 border-t border-white/[0.06] p-2">
          <Button
            size="sm"
            variant={next.primary ? "primary" : "secondary"}
            className="flex-1 [&_svg]:size-3.5"
            title={next.title}
            loading={archived && drive.isPending}
            onClick={next.onClick}
          >
            {next.icon}
            {next.label}
          </Button>

          {archived ? (
            <Button
              size="sm"
              variant="ghost"
              title="Open its folder in Google Drive"
              onClick={() => reveal.mutate(m.runId)}
            >
              <FolderOpen className="size-3.5" /> Open
            </Button>
          ) : (
            <Button
              size="sm"
              variant="ghost"
              disabled={!driveReady || !onMoveToDrive}
              title={driveReady
                ? "Move this video's files to Google Drive"
                : "Set up Google Drive in Settings → Storage first"}
              onClick={() => onMoveToDrive?.(m)}
            >
              <CloudUpload className="size-3.5" /> Drive
            </Button>
          )}

          <Menu>
            {!archived && (
              <>
                <MenuLabel>Redo</MenuLabel>
                <MenuItem
                  icon={<Volume2 />}
                  disabled={!st.voice.done}
                  title={st.voice.done ? "Generate the voiceover again — picks up a changed voice or model" : "Generate the voiceover first"}
                  onSelect={() => regen("voice")}
                >
                  Voiceover
                </MenuItem>
                <MenuItem
                  icon={<Film />}
                  disabled={!complete}
                  title={complete ? "Re-assemble the mp4 with the current frames and voice" : "Assemble the video first"}
                  onSelect={() => regen("video")}
                >
                  Re-assemble video{musicOn ? "" : " (no music)"}
                </MenuItem>
                <MenuItem
                  icon={musicOn ? <VolumeX /> : <Music />}
                  disabled={!complete}
                  title={musicOn
                    ? "Re-assemble voice-only, and drop the music credit from the description"
                    : "Re-assemble with background music, and put its credit back in the description"}
                  onSelect={() => regen("video", !musicOn)}
                >
                  {musicOn ? "Re-assemble without music" : "Re-assemble with music"}
                </MenuItem>

                <MenuDivider />
                <MenuLabel>Publish</MenuLabel>
                <MenuItem icon={<MonitorPlay />} disabled={!canPublish} title={gateReason || statusTip("YouTube", st.upload)} onSelect={() => publish("upload")}>
                  YouTube{st.upload.done ? " ✓" : ""}
                </MenuItem>
                <MenuItem icon={<ThumbsUp />} disabled={!canPublish} title={gateReason || statusTip("Facebook", st.facebook)} onSelect={() => publish("facebook")}>
                  Facebook{st.facebook.done ? " ✓" : ""}
                </MenuItem>
                <MenuItem icon={<Camera />} disabled={!canPublish} title={gateReason || statusTip("Instagram", st.instagram)} onSelect={() => publish("instagram")}>
                  Instagram{st.instagram.done ? " ✓" : ""}
                </MenuItem>
                <MenuDivider />
              </>
            )}
            <MenuItem
              icon={<Trash2 />}
              danger
              disabled={!onDelete}
              title={archived ? "Removes it from this list. The copy in Google Drive stays." : "Delete this video and all its files"}
              onSelect={() => onDelete?.(m)}
            >
              {archived ? "Remove from list" : "Delete video"}
            </MenuItem>
          </Menu>
        </div>
      )}
    </Card>
  );
}

/** Tooltip text carrying the last outcome, including why a publish did nothing. */
function statusTip(platform: string, s: StageStatus) {
  if (s.done) return `${platform}: published`;
  if (s.skipReason) return `${platform} skipped — ${s.skipReason}`;
  return `Publish to ${platform}`;
}
