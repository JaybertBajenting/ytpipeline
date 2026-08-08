import { CheckCircle2, Film, Image as ImageIcon, Mic, Trash2, Volume2 } from "lucide-react";
import { api, stagesDone, type Manifest } from "@/lib/api";
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
 * One video in the grid. Clicking it opens the detail view — the whole card is the target,
 * so there's no small "open" affordance to hunt for.
 */
export function VideoCard({
  m, index, onDelete,
}: { m: Manifest; index: number; onDelete?: (m: Manifest) => void }) {
  const select = useUi((s) => s.select);
  const running = useUi((s) => !!s.jobs[m.runId]);

  const done = stagesDone(m);
  const st = m.stages;
  const complete = st.video.done;
  const title = m.publish.title || m.topic;

  return (
    <Card
      interactive
      tone={running ? "run" : "base"}
      className="stagger group flex flex-col overflow-hidden"
      style={{ "--i": index } as React.CSSProperties}
      onClick={() => select(m.runId)}
    >
      {/* ── Thumbnail / preview ─────────────────────────────────────────── */}
      <div className="relative aspect-video overflow-hidden bg-black/40">
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

        {/* Status corner */}
        <div className="absolute left-2 top-2 flex gap-1.5">
          <Badge tone="channel" className="backdrop-blur-sm">{m.channel}</Badge>
        </div>
        <div className="absolute right-2 top-2 flex items-center gap-1.5">
          {/* Delete appears on hover — always visible would invite mis-clicks on a card
              whose whole surface is already a link. Hidden entirely while a job runs. */}
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

        {/* Progress hugs the bottom edge of the preview. */}
        <div className="absolute inset-x-0 bottom-0">
          <Progress value={done} total={5} indeterminate={running} className="h-[3px] rounded-none" />
        </div>
      </div>

      {/* ── Body ────────────────────────────────────────────────────────── */}
      <div className="flex flex-1 flex-col gap-2.5 p-3.5">
        <div className="min-w-0 flex-1">
          <h3 className="clamp-2 text-[13px] font-semibold leading-snug text-white/90">
            {title}
          </h3>
          {m.publish.title && m.publish.title !== m.topic && (
            <p className="clamp-2 mt-1 text-[11px] leading-relaxed text-white/30">{m.topic}</p>
          )}
        </div>

        {/* Stage dots */}
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

        {/* Footer facts */}
        <div className="flex items-center gap-3 border-t border-white/[0.05] pt-2 text-[10px] text-white/30">
          {st.images.count ? (
            <span className="flex items-center gap-1">
              <ImageIcon className="size-2.5" /> {st.images.count}
            </span>
          ) : null}
          {st.voice.seconds ? (
            <span className="flex items-center gap-1">
              <Volume2 className="size-2.5" /> {Math.round(st.voice.seconds)}s
            </span>
          ) : null}
          {st.upload.done && <Badge tone="info">uploaded</Badge>}
        </div>
      </div>
    </Card>
  );
}
