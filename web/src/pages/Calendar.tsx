import { useMemo, useState } from "react";
import {
  CalendarDays, CheckCircle2, ChevronLeft, ChevronRight, Clock, Inbox, X,
} from "lucide-react";
import { stagesDone, type Manifest } from "@/lib/api";
import { useRuns, useScheduleRun } from "@/lib/hooks";
import { useUi } from "@/lib/store";
import {
  Badge, Button, Callout, Card, CardHead, EmptyState, PageHead, Skeleton, StatTile,
} from "@/components/ui";
import { cn } from "@/lib/utils";

/** Local YYYY-MM-DD key. Deliberately not toISOString, which would shift the day by timezone. */
function dayKey(d: Date) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

export function Calendar() {
  const { data: runs, isLoading } = useRuns();
  const select = useUi((s) => s.select);
  const schedule = useScheduleRun();
  const [cursor, setCursor] = useState(() => new Date());

  const scheduled = useMemo(
    () => (runs ?? []).filter((m) => !!m.publish.scheduledUtc),
    [runs],
  );

  /** Scheduled runs bucketed by local day. */
  const byDay = useMemo(() => {
    const map = new Map<string, Manifest[]>();
    for (const m of scheduled) {
      const k = dayKey(new Date(m.publish.scheduledUtc!));
      (map.get(k) ?? map.set(k, []).get(k)!).push(m);
    }
    return map;
  }, [scheduled]);

  /** Finished but undated — the backlog worth dragging onto a day. */
  const backlog = useMemo(
    () => (runs ?? [])
      .filter((m) => !m.publish.scheduledUtc && m.stages.video.done && !m.stages.upload.done)
      .sort((a, b) => +new Date(b.createdUtc) - +new Date(a.createdUtc)),
    [runs],
  );

  // Monday-first grid covering the whole month plus the leading/trailing days that fill the weeks.
  const grid = useMemo(() => {
    const first = new Date(cursor.getFullYear(), cursor.getMonth(), 1);
    const offset = (first.getDay() + 6) % 7;          // JS weeks start Sunday; we want Monday.
    const start = new Date(first);
    start.setDate(first.getDate() - offset);
    return Array.from({ length: 42 }, (_, i) => {
      const d = new Date(start);
      d.setDate(start.getDate() + i);
      return d;
    });
  }, [cursor]);

  const todayKey = dayKey(new Date());
  const monthLabel = cursor.toLocaleDateString(undefined, { month: "long", year: "numeric" });

  const overdue = scheduled.filter(
    (m) => new Date(m.publish.scheduledUtc!) < new Date() && !m.stages.upload.done);

  if (isLoading) return <Skeleton className="h-96" />;

  return (
    <>
      <PageHead
        title="Calendar"
        desc="Your publish plan. Dates are a plan only — nothing uploads on a timer."
      />

      <div className="mb-5 grid gap-3 sm:grid-cols-3">
        <StatTile
          label="Scheduled" value={scheduled.length}
          sub="have a planned date" icon={<CalendarDays className="size-3.5" />}
        />
        <StatTile
          label="Past due" value={overdue.length}
          sub={overdue.length ? "planned date has passed" : "nothing overdue"}
          tone={overdue.length ? "amber" : "base"}
          icon={<Clock className="size-3.5" />}
        />
        <StatTile
          label="Ready, undated" value={backlog.length}
          sub="finished but not planned" icon={<Inbox className="size-3.5" />}
        />
      </div>

      {overdue.length > 0 && (
        <div className="mb-5">
          <Callout tone="warn" title={`${overdue.length} video(s) past their planned date`}>
            Publishing is manual — a date passing doesn't upload anything. Open a video and use
            its publish buttons, or the buttons on its card.
          </Callout>
        </div>
      )}

      <div className="grid gap-5 lg:grid-cols-[1fr_290px]">
        {/* ── Month grid ──────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title={monthLabel}
            icon={<CalendarDays className="size-3.5" />}
            right={
              <div className="flex items-center gap-1">
                <Button
                  size="icon" variant="ghost" title="Previous month"
                  onClick={() => setCursor(new Date(cursor.getFullYear(), cursor.getMonth() - 1, 1))}
                >
                  <ChevronLeft className="size-4" />
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setCursor(new Date())}>Today</Button>
                <Button
                  size="icon" variant="ghost" title="Next month"
                  onClick={() => setCursor(new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1))}
                >
                  <ChevronRight className="size-4" />
                </Button>
              </div>
            }
          />
          <div className="p-3">
            <div className="mb-1 grid grid-cols-7 gap-1">
              {WEEKDAYS.map((d) => (
                <div key={d} className="pb-1 text-center text-[10px] font-semibold uppercase tracking-wider text-white/25">
                  {d}
                </div>
              ))}
            </div>

            <div className="grid grid-cols-7 gap-1">
              {grid.map((d) => {
                const k = dayKey(d);
                const items = byDay.get(k) ?? [];
                const inMonth = d.getMonth() === cursor.getMonth();
                const isToday = k === todayKey;

                return (
                  <div
                    key={k}
                    className={cn(
                      "min-h-[74px] rounded-lg border p-1.5 transition-colors",
                      isToday
                        ? "border-amber-500/40 bg-amber-500/[0.06]"
                        : "border-white/[0.05] bg-white/[0.015]",
                      !inMonth && "opacity-35",
                    )}
                  >
                    <div className={cn(
                      "mb-1 text-[10px] font-semibold tabular-nums",
                      isToday ? "text-amber-400" : "text-white/35",
                    )}>
                      {d.getDate()}
                    </div>

                    <div className="space-y-1">
                      {items.slice(0, 2).map((m) => (
                        <button
                          key={m.runId}
                          onClick={() => select(m.runId)}
                          title={`${m.publish.title || m.topic} · ${m.channel}`}
                          className={cn(
                            "block w-full truncate rounded px-1 py-0.5 text-left text-[9px] font-medium transition-colors",
                            m.stages.upload.done
                              ? "bg-emerald-500/15 text-emerald-300 hover:bg-emerald-500/25"
                              : "bg-sky-500/15 text-sky-200 hover:bg-sky-500/25",
                          )}
                        >
                          {m.stages.upload.done && "✓ "}
                          {m.publish.title || m.topic}
                        </button>
                      ))}
                      {items.length > 2 && (
                        <div className="px-1 text-[9px] text-white/30">+{items.length - 2} more</div>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        </Card>

        {/* ── Backlog: assign a date ──────────────────────────────────────── */}
        <div className="space-y-4">
          <Card>
            <CardHead
              title="Ready, undated"
              desc="Finished videos with no planned date."
              icon={<Inbox className="size-3.5" />}
            />
            <div className="p-3">
              {backlog.length === 0 ? (
                <p className="px-1 py-2 text-xs text-white/30">
                  Nothing waiting — every finished video has a date or is already uploaded.
                </p>
              ) : (
                <div className="space-y-2">
                  {backlog.map((m) => (
                    <ScheduleRow
                      key={m.runId}
                      m={m}
                      onOpen={() => select(m.runId)}
                      onSet={(iso) => schedule.mutate({ runId: m.runId, scheduledUtc: iso })}
                      pending={schedule.isPending}
                    />
                  ))}
                </div>
              )}
            </div>
          </Card>

          {scheduled.length > 0 && (
            <Card>
              <CardHead title="Scheduled" desc="Click a date to clear it." icon={<CalendarDays className="size-3.5" />} />
              <div className="space-y-1.5 p-3">
                {[...scheduled]
                  .sort((a, b) => +new Date(a.publish.scheduledUtc!) - +new Date(b.publish.scheduledUtc!))
                  .map((m) => (
                    <div key={m.runId} className="flex items-center gap-2 rounded-lg px-1.5 py-1 hover:bg-white/[0.04]">
                      <button onClick={() => select(m.runId)} className="min-w-0 flex-1 text-left">
                        <span className="block truncate text-[11px] font-medium text-white/80">
                          {m.publish.title || m.topic}
                        </span>
                        <span className="block text-[10px] text-white/30">
                          {new Date(m.publish.scheduledUtc!).toLocaleDateString(undefined,
                            { weekday: "short", month: "short", day: "numeric" })} · {m.channel}
                        </span>
                      </button>
                      {m.stages.upload.done
                        ? <CheckCircle2 className="size-3 shrink-0 text-emerald-400" />
                        : (
                          <button
                            title="Clear the planned date"
                            onClick={() => schedule.mutate({ runId: m.runId, scheduledUtc: null })}
                            className="shrink-0 rounded p-0.5 text-white/25 transition-colors hover:bg-red-500/15 hover:text-red-400"
                          >
                            <X className="size-3" />
                          </button>
                        )}
                    </div>
                  ))}
              </div>
            </Card>
          )}
        </div>
      </div>

      {scheduled.length === 0 && backlog.length === 0 && (
        <div className="mt-5">
          <EmptyState
            icon={<CalendarDays className="size-6" />}
            title="Nothing to plan yet"
            desc="Once a video reaches the assembled-and-approved stage it shows up here, ready to be given a publish date."
          />
        </div>
      )}
    </>
  );
}

/** A backlog row with a native date picker — dates are coarse, so a full picker UI isn't worth it. */
function ScheduleRow({
  m, onOpen, onSet, pending,
}: { m: Manifest; onOpen: () => void; onSet: (iso: string | null) => void; pending: boolean }) {
  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-2">
      <button onClick={onOpen} className="block w-full text-left">
        <span className="clamp-2 block text-[11px] font-medium leading-snug text-white/80">
          {m.publish.title || m.topic}
        </span>
        <span className="mt-0.5 flex items-center gap-1.5">
          <Badge tone="channel">{m.channel}</Badge>
          <span className="text-[10px] text-white/25">{stagesDone(m)}/5</span>
        </span>
      </button>
      <input
        type="date"
        disabled={pending}
        className="mt-2 w-full rounded-md border border-white/[0.08] bg-white/[0.04] px-2 py-1 text-[11px] text-white/80 outline-none focus:border-amber-500/50 [color-scheme:dark]"
        onChange={(e) => {
          if (!e.target.value) return onSet(null);
          // Noon local keeps the date stable when it round-trips through UTC.
          onSet(new Date(`${e.target.value}T12:00:00`).toISOString());
        }}
      />
    </div>
  );
}
