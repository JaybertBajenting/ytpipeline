import {
  CalendarDays, Camera, CheckCircle2, Film, Image as ImageIcon, Mic, MonitorPlay,
  Rocket, ThumbsUp, Trash2, Volume2,
} from "lucide-react";
import type { ReactNode } from "react";
import { api, stagesDone, type Manifest, type StageName, type StageStatus } from "@/lib/api";
import { useRunStage } from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { Badge, Card, Progress, Spinner } from "./ui";
import { cn } from "@/lib/utils";

const STAGE_ICONS = [
  { key: "script", label: "Script", Icon: Mic },
  { key: "voice",  label: "Voice",  Icon: Volume2 },
  { key: "images", label: "Images", Icon: ImageIcon },
  { key: "video",  label: "Video",  Icon: Film },
] as const;

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

/**
 * One video in the grid. The preview and body open the detail view; the publish row does not,
 * so the whole-card click target and the per-platform actions don't fight each other.
 */
export function VideoCard({
  m, index, onDelete,
}: { m: Manifest; index: number; onDelete?: (m: Manifest) => void }) {
  const select = useUi((s) => s.select);
  const running = useUi((s) => !!s.jobs[m.runId]);
  const stage = useRunStage(m.runId);

  const done = stagesDone(m);
  const st = m.stages;
  const complete = st.video.done;
  const title = m.publish.title || m.topic;

  // Publishing posts publicly, so it stays behind the video checkpoint — same gate the detail
  // view uses.
  const canPublish = st.video.done && m.review.videoApproved && !running;
  const gateReason = !st.video.done
    ? "Assemble the video first"
    : !m.review.videoApproved
      ? "Approve the video first"
      : running
        ? "A job is already running"
        : "";

  const run = (s: StageName) => { if (canPublish) stage.mutate({ stage: s }); };

  const openDetail = () => select(m.runId);

  return (
    <Card
      tone={running ? "run" : "base"}
      className="stagger group flex flex-col overflow-hidden"
      style={{ "--i": index } as React.CSSProperties}
    >
      {/* ── Thumbnail / preview (opens detail) ──────────────────────────── */}
      <div
        className="relative aspect-video cursor-pointer overflow-hidden bg-black/40"
        onClick={openDetail}
      >
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

        <div className="absolute left-2 top-2 flex gap-1.5">
          <Badge tone="channel" className="backdrop-blur-sm">{m.channel}</Badge>
        </div>

        <div className="absolute right-2 top-2 flex items-center gap-1.5">
          {onDelete && !running && (
            <button
              title="Delete this video"
              onClick={(e) => { e.stopPropagation(); onDelete(m); }}
              className="grid size-6 place-items-center rounded-md bg-black/50 text-white/50 opacity-0 backdrop-blur-sm transition-all hover:bg-red-500/25 hover:text-red-300 focus-visible:opacity-100 group-hover:opacity-100"
            >
              <Trash2 className="size-3" />
            </button>
          )}
          {running
            ? <Badge tone="run" className="backdrop-blur-sm"><Spinner className="size-2.5" /> running</Badge>
            : complete
              ? <Badge tone="ok" className="backdrop-blur-sm"><CheckCircle2 className="size-2.5" /> ready</Badge>
              : <Badge tone="muted" className="backdrop-blur-sm">{done}/5</Badge>}
        </div>

        <div className="absolute inset-x-0 bottom-0">
          <Progress value={done} total={5} indeterminate={running} className="h-[3px] rounded-none" />
        </div>
      </div>

      {/* ── Body (opens detail) ─────────────────────────────────────────── */}
      <div className="flex flex-1 cursor-pointer flex-col gap-2.5 p-3.5" onClick={openDetail}>
        <div className="min-w-0 flex-1">
          <h3 className="clamp-2 text-[13px] font-semibold leading-snug text-white/90">{title}</h3>
          {m.publish.title && m.publish.title !== m.topic && (
            <p className="clamp-2 mt-1 text-[11px] leading-relaxed text-white/30">{m.topic}</p>
          )}
        </div>

        <div className="flex items-center gap-1">
          {STAGE_ICONS.map(({ key, label, Icon }) => {
            const ok = st[key].done;
            return (
              <span
                key={key}
                title={`${label}: ${ok ? "done" : "pending"}`}
                className={cn(
                  "grid size-6 place-items-center rounded-md transition-colors",
                  ok ? "bg-emerald-500/12 text-emerald-400" : "bg-white/[0.04] text-white/20",
                )}
              >
                <Icon className="size-3" />
              </span>
            );
          })}
          <span className="ml-auto text-[10px] tabular-nums text-white/25">
            {relative(m.createdUtc)}
          </span>
        </div>

        <div className="flex items-center gap-3 border-t border-white/[0.05] pt-2 text-[10px] text-white/30">
          {st.images.count ? (
            <span className="flex items-center gap-1"><ImageIcon className="size-2.5" /> {st.images.count}</span>
          ) : null}
          {st.voice.seconds ? (
            <span className="flex items-center gap-1"><Volume2 className="size-2.5" /> {Math.round(st.voice.seconds)}s</span>
          ) : null}
          {m.publish.scheduledUtc && (
            <span className="flex items-center gap-1 text-sky-300/70">
              <CalendarDays className="size-2.5" />
              {new Date(m.publish.scheduledUtc).toLocaleDateString(undefined, { month: "short", day: "numeric" })}
            </span>
          )}
        </div>
      </div>

      {/* ── Publish row (does NOT open detail) ──────────────────────────── */}
      <div
        className="grid grid-cols-4 gap-1 border-t border-white/[0.06] bg-white/[0.015] p-1.5"
        title={gateReason}
      >
        <PublishButton
          label="All" icon={<Rocket className="size-3" />} primary
          disabled={!canPublish}
          onClick={() => run("publish-all")}
          tip={gateReason || "Publish to every configured target"}
        />
        <PublishButton
          label="YT" icon={<MonitorPlay className="size-3" />}
          status={st.upload} disabled={!canPublish}
          onClick={() => run("upload")}
          tip={gateReason || statusTip("YouTube", st.upload)}
        />
        <PublishButton
          label="FB" icon={<ThumbsUp className="size-3" />}
          status={st.facebook} disabled={!canPublish}
          onClick={() => run("facebook")}
          tip={gateReason || statusTip("Facebook", st.facebook)}
        />
        <PublishButton
          label="IG" icon={<Camera className="size-3" />}
          status={st.instagram} disabled={!canPublish}
          onClick={() => run("instagram")}
          tip={gateReason || statusTip("Instagram", st.instagram)}
        />
      </div>
    </Card>
  );
}

/** Tooltip text carrying the last outcome, including why a publish did nothing. */
function statusTip(platform: string, s: StageStatus) {
  if (s.done) return `${platform}: published`;
  if (s.skipReason) return `${platform} skipped — ${s.skipReason}`;
  return `Publish to ${platform}`;
}

function PublishButton({
  label, icon, status, disabled, onClick, tip, primary,
}: {
  label: string; icon: ReactNode; status?: StageStatus;
  disabled?: boolean; onClick: () => void; tip: string; primary?: boolean;
}) {
  const published = status?.done;
  const skipped = !published && !!status?.skipReason;

  return (
    <button
      title={tip}
      disabled={disabled}
      onClick={(e) => { e.stopPropagation(); onClick(); }}
      className={cn(
        "relative flex items-center justify-center gap-1 rounded-md px-1 py-1.5 text-[10px] font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-35",
        primary
          ? "bg-amber-500/15 text-amber-400 hover:bg-amber-500/25"
          : published
            ? "bg-emerald-500/12 text-emerald-400 hover:bg-emerald-500/20"
            : skipped
              ? "bg-amber-500/[0.08] text-amber-400/70 hover:bg-amber-500/15"
              : "text-white/45 hover:bg-white/[0.07] hover:text-white/80",
      )}
    >
      {icon}
      {label}
      {published && (
        <span className="absolute right-1 top-1 size-1 rounded-full bg-emerald-400" />
      )}
    </button>
  );
}
