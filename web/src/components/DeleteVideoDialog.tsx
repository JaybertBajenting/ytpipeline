import { formatBytes, type Manifest } from "@/lib/api";
import { useDeleteRun, useRun } from "@/lib/hooks";
import { Callout, ConfirmModal, KeyValue } from "./ui";

/**
 * Delete confirmation for a run. Spells out what's on disk before removing it — generated
 * frames are paid quota, so "are you sure?" alone isn't enough information to answer.
 * Requires typing the channel name, because the grid puts this button next to 17 near-identical
 * cards.
 */
export function DeleteVideoDialog({
  target, onClose,
}: { target: Manifest | null; onClose: () => void }) {
  const del = useDeleteRun();
  // Cached if the detail view was open; otherwise this is the one fetch that gets the size.
  const detail = useRun(target?.runId ?? null);

  if (!target) return null;

  const frames = detail.data?.images.length ?? target.stages.images.count ?? 0;
  const size = detail.data?.sizeBytes;
  const title = target.publish.title || target.topic;

  async function confirm() {
    await del.mutateAsync(target!.runId);
    onClose();
  }

  return (
    <ConfirmModal
      open
      onClose={onClose}
      onConfirm={confirm}
      loading={del.isPending}
      title="Delete this video?"
      confirmLabel="Delete permanently"
      confirmText={target.channel}
    >
      <p className="text-sm text-white/70">{title}</p>

      <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] px-3 py-2">
        <KeyValue k="Run" v={<code className="font-mono text-[10px]">{target.runId}</code>} />
        <KeyValue k="Channel" v={target.channel} />
        <KeyValue k="Frames" v={frames || "—"} />
        <KeyValue k="Voiceover" v={target.stages.voice.seconds ? `${Math.round(target.stages.voice.seconds)}s` : "—"} />
        <KeyValue k="Final video" v={target.stages.video.done ? "yes" : "no"} />
        <KeyValue k="On disk" v={size !== undefined ? formatBytes(size) : "…"} />
      </div>

      {target.archive && (
        <Callout tone="info">
          This video's files are in Google Drive. Only its entry on this PC is removed — the copy in
          Google Drive stays where it is.
        </Callout>
      )}

      {frames > 0 && !target.archive && (
        <Callout tone="warn" title="Frames cost quota to regenerate">
          Deleting removes {frames} generated image{frames === 1 ? "" : "s"}. Re-creating them
          means spending image credits again — there's no undo and no recycle bin.
        </Callout>
      )}

      {target.stages.upload.done && (
        <Callout tone="info">
          This run was already uploaded. Deleting the local folder does not remove the published
          video from the platform.
        </Callout>
      )}

      {del.isError && <Callout tone="err">{(del.error as Error).message}</Callout>}
    </ConfirmModal>
  );
}
