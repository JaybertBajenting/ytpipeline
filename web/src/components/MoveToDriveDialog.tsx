import { formatBytes, type Manifest } from "@/lib/api";
import { useDriveTransfer, useRun, useSettings } from "@/lib/hooks";
import { Callout, ConfirmModal, KeyValue } from "./ui";

/**
 * Confirms moving a video's files into Google Drive. Says where they land and how much it frees,
 * because the move removes the copies on this PC once Drive has them.
 */
export function MoveToDriveDialog({
  target, onClose,
}: { target: Manifest | null; onClose: () => void }) {
  const { data: settings } = useSettings();
  // Cached if the detail view was open; otherwise this is the one fetch that gets the size.
  const detail = useRun(target?.runId ?? null);
  const move = useDriveTransfer(target?.runId ?? "");

  if (!target) return null;

  const stamp = target.runId.startsWith(`${target.channel}_`)
    ? target.runId.slice(target.channel.length + 1)
    : target.runId;
  // Same choice the server makes: an rclone remote wins, else the Drive app's folder.
  const remote = settings?.rcloneRemote ?? "";
  const destination = remote
    ? `${remote.replace(/[\\/]+$/, "")}/${target.channel}/${stamp}`
    : `${settings?.driveArchiveFolder ?? ""}\\${target.channel}\\${stamp}`;
  const size = detail.data?.sizeBytes;
  // The server explains refusals in `error`; axios's own message only names the status code.
  const error = move.error
    ? ((move.error as { response?: { data?: { error?: string } } }).response?.data?.error
      ?? (move.error as Error).message)
    : null;

  return (
    <ConfirmModal
      open
      onClose={onClose}
      onConfirm={() => move.mutate("move", { onSuccess: onClose })}
      loading={move.isPending}
      danger={false}
      title="Move this video to Google Drive?"
      confirmLabel="Move to Google Drive"
    >
      <p className="text-sm text-white/70">{target.topic || target.publish.title}</p>

      <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] px-3 py-2">
        <KeyValue k="Goes to" v={<code className="break-all font-mono text-[10px]">{destination}</code>} />
        <KeyValue k="Frees on this PC" v={size !== undefined ? formatBytes(size) : "…"} />
      </div>

      <Callout tone="info">
        {remote
          ? "Uploaded straight to Google Drive and checked file by file; only then is it removed here, so the space comes back right away."
          : "Every file is copied and checked before anything is removed here. Google Drive for desktop may keep a cached copy on this PC for a while."}{" "}
        The video stays in your list, and Bring back copies it home again whenever you need it.
      </Callout>

      {error && <Callout tone="err">{error}</Callout>}
    </ConfirmModal>
  );
}
