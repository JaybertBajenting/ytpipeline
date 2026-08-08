import { useEffect, useRef } from "react";
import { Square, X } from "lucide-react";
import type { Job } from "@/lib/api";
import { Badge, Button, Spinner } from "./ui";
import { cn } from "@/lib/utils";

function colorLine(line: string) {
  if (/\[(WRN|WAR)\]/.test(line)) return "log-wrn";
  if (/\[(ERR|FAT)\]/.test(line)) return "log-err";
  if (/^\-\-\- stopped/.test(line)) return "log-err";
  return "log-inf";
}

export function JobLog({
  job, title, onStop, onClose,
}: {
  job: Job | undefined;
  title: string;
  onStop?: () => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLPreElement>(null);

  useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [job?.logs?.length]);

  if (!job) return null;

  const running = job.status === "running";

  return (
    <div className="fixed bottom-4 right-4 z-50 w-[500px] max-w-[calc(100vw-2rem)] overflow-hidden rounded-xl border border-white/[0.1] bg-[#0a0a12] shadow-2xl shadow-black/60">

      {/* ── Terminal title bar ─────────────────────────────────────────── */}
      <div className="flex items-center gap-2 border-b border-white/[0.07] bg-white/[0.03] px-3 py-2">
        {/* Traffic light dots */}
        <div className="flex items-center gap-1.5">
          <span className="size-2.5 rounded-full bg-white/[0.12]" />
          <span className="size-2.5 rounded-full bg-white/[0.12]" />
          <span className="size-2.5 rounded-full bg-white/[0.12]" />
        </div>

        <span className="flex-1 truncate font-mono text-xs text-white/40">{title}</span>

        {running ? (
          <Badge tone="run"><Spinner /> running</Badge>
        ) : job.status === "succeeded" ? (
          <Badge tone="ok">done</Badge>
        ) : (
          <Badge tone="err">failed</Badge>
        )}

        {/* Stop button — only while running */}
        {running && onStop && (
          <Button
            variant="danger"
            size="xs"
            onClick={onStop}
            title="Stop this job"
          >
            <Square className="size-3 fill-current" /> Stop
          </Button>
        )}

        <button
          className="ml-1 rounded p-0.5 text-white/25 transition-colors hover:bg-white/[0.08] hover:text-white/60"
          onClick={onClose}
          title="Dismiss"
        >
          <X className="size-3.5" />
        </button>
      </div>

      {/* ── Log output ────────────────────────────────────────────────── */}
      <pre
        ref={ref}
        className="m-0 max-h-56 overflow-auto px-3 py-2.5 font-mono text-[11px] leading-relaxed"
      >
        {job.logs.map((line, i) => (
          <div key={i} className={colorLine(line)}>{line}</div>
        ))}
        {job.error && !job.logs.some(l => l.includes(job.error!)) && (
          <div className="log-err">{job.error}</div>
        )}
        {running && (
          <div className="mt-1 flex items-center gap-1.5 text-amber-400/70">
            <Spinner className="size-3" /> processing…
          </div>
        )}
      </pre>
    </div>
  );
}
