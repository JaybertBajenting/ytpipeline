import { useMemo, useState } from "react";
import { Film, Plus, Search } from "lucide-react";
import { stagesDone, type Manifest } from "@/lib/api";
import { useChannels, useRuns } from "@/lib/hooks";
import { useUi, type VideoStatusFilter as Filter } from "@/lib/store";
import { VideoCard } from "@/components/VideoCard";
import { RunDetail } from "@/components/RunDetail";
import { DeleteVideoDialog } from "@/components/DeleteVideoDialog";
import { MoveToDriveDialog } from "@/components/MoveToDriveDialog";
import { Button, EmptyState, Input, PageHead, Select, Skeleton } from "@/components/ui";

export function Videos() {
  const selectedRunId = useUi((s) => s.selectedRunId);
  const setComposerOpen = useUi((s) => s.setComposerOpen);
  const { data: runs, isLoading } = useRuns();
  const { data: channels } = useChannels();

  // Filters live in the UI store, not local state: this page unmounts whenever another sidebar
  // destination opens, and local state was thrown away with it.
  const q = useUi((s) => s.videoQuery);
  const setQ = useUi((s) => s.setVideoQuery);
  const savedChannel = useUi((s) => s.videoChannel);
  const setChannel = useUi((s) => s.setVideoChannel);
  const filter = useUi((s) => s.videoStatus);
  const setFilter = useUi((s) => s.setVideoStatus);
  const [deleting, setDeleting] = useState<Manifest | null>(null);
  const [moving, setMoving] = useState<Manifest | null>(null);

  // A remembered channel can outlive the channel. Trust it until the list loads, so a reload
  // doesn't flash every video first; after that, a deleted channel falls back to "all" instead
  // of an empty grid behind a select that can't display what it's filtering on.
  const channel = savedChannel !== "all" && channels && !channels.some((c) => c.id === savedChannel)
    ? "all"
    : savedChannel;

  const shown = useMemo(() => {
    let list = runs ?? [];
    if (channel !== "all") list = list.filter((m) => m.channel === channel);
    if (filter === "in-progress") list = list.filter((m) => !m.stages.video.done);
    if (filter === "ready") list = list.filter((m) => m.stages.video.done && !m.stages.upload.done);
    if (filter === "uploaded") list = list.filter((m) => m.stages.upload.done);
    const needle = q.trim().toLowerCase();
    if (needle) {
      list = list.filter((m) =>
        (m.publish.title || "").toLowerCase().includes(needle) ||
        m.topic.toLowerCase().includes(needle) ||
        m.runId.toLowerCase().includes(needle));
    }
    // Newest first — createdUtc is the run id's timestamp, so this matches folder order.
    return [...list].sort((a, b) => +new Date(b.createdUtc) - +new Date(a.createdUtc));
  }, [runs, channel, filter, q]);

  // A selected run takes over the whole view — the grid is the "browse" mode and the detail is
  // the "work on it" mode, and mixing both in one screen made neither readable.
  //
  // MUST stay below every hook above. Returning before a hook changes the hook count between
  // renders, which React rejects outright (error #300) and blanks the whole page.
  if (selectedRunId) return <RunDetail runId={selectedRunId} />;

  return (
    <>
      <PageHead
        title="Videos"
        desc={runs ? `${runs.length} run${runs.length === 1 ? "" : "s"} across ${new Set(runs.map((r) => r.channel)).size} channel(s)` : undefined}
        actions={
          <Button variant="primary" onClick={() => setComposerOpen(true)}>
            <Plus className="size-4" strokeWidth={2.5} /> New video
          </Button>
        }
      />

      {/* ── Filters ───────────────────────────────────────────────────────── */}
      <div className="mb-5 flex flex-wrap items-end gap-2">
        <div className="relative min-w-[200px] flex-1">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-white/25" />
          <Input
            className="pl-9"
            placeholder="Search title, topic or run id…"
            value={q}
            onChange={(e) => setQ(e.target.value)}
          />
        </div>
        <Select value={channel} onChange={(e) => setChannel(e.target.value)} className="min-w-[150px]">
          <option value="all">All channels</option>
          {channels?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </Select>
        <Select value={filter} onChange={(e) => setFilter(e.target.value as Filter)} className="min-w-[140px]">
          <option value="all">Any status</option>
          <option value="in-progress">In progress</option>
          <option value="ready">Ready to upload</option>
          <option value="uploaded">Uploaded</option>
        </Select>
      </div>

      {/* ── Grid ──────────────────────────────────────────────────────────── */}
      {isLoading ? (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(250px,1fr))] gap-4">
          {Array.from({ length: 6 }).map((_, i) => (
            <div key={i} className="overflow-hidden rounded-xl border border-white/[0.07]">
              <Skeleton className="aspect-video rounded-none" />
              <div className="space-y-2 p-3.5">
                <Skeleton className="h-3 w-4/5" />
                <Skeleton className="h-3 w-1/2" />
              </div>
            </div>
          ))}
        </div>
      ) : shown.length === 0 ? (
        <EmptyState
          icon={<Film className="size-6" />}
          title={runs?.length ? "Nothing matches those filters" : "No videos yet"}
          desc={runs?.length
            ? "Try clearing the search or switching the channel filter."
            : "Start a run and the script, voiceover, frames and final cut all land here."}
          action={runs?.length
            ? <Button onClick={() => { setQ(""); setChannel("all"); setFilter("all"); }}>Clear filters</Button>
            : <Button variant="primary" onClick={() => setComposerOpen(true)}>
                <Plus className="size-4" /> New video
              </Button>}
        />
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(250px,1fr))] gap-4">
          {shown.map((m: Manifest, i) => (
            <VideoCard key={m.runId} m={m} index={i} onDelete={setDeleting} onMoveToDrive={setMoving} />
          ))}
        </div>
      )}

      {shown.length > 0 && (
        <p className="mt-6 text-center text-[11px] text-white/20">
          {shown.length} of {runs?.length} · {shown.filter((m) => stagesDone(m) === 5).length} fully complete
        </p>
      )}

      <DeleteVideoDialog target={deleting} onClose={() => setDeleting(null)} />
      <MoveToDriveDialog target={moving} onClose={() => setMoving(null)} />
    </>
  );
}
