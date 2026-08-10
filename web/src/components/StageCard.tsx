import { ChevronDown } from "lucide-react";
import type { ReactNode } from "react";
import { useUi } from "@/lib/store";
import { PipelineNode } from "./ui";
import { cn } from "@/lib/utils";

export function StageCard({
  stageKey, num, title, subtitle, done, locked, running, status, children, actions,
  defaultCollapsed = false,
}: {
  stageKey: string;
  num: ReactNode;
  title: string;
  /** One line of detail under the title — keeps the header informative while closed. */
  subtitle?: ReactNode;
  done?: boolean;
  locked?: boolean;
  running?: boolean;
  status?: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
  /**
   * Whether this card starts closed when the user hasn't chosen. Finished stages default to
   * closed: opening a video used to dump the whole script, every thumbnail and every panel at
   * once, which buried the one thing you actually came to do.
   */
  defaultCollapsed?: boolean;
}) {
  const explicitlyCollapsed = useUi((s) => s.collapsed.has(stageKey));
  const explicitlyExpanded = useUi((s) => s.expanded.has(stageKey));
  const toggle = useUi((s) => s.toggleCollapsed);

  const open = explicitlyExpanded ? true
    : explicitlyCollapsed ? false
    : !defaultCollapsed;

  const hasBody = !!children || !!actions;

  return (
    <div className={cn(
      "overflow-hidden rounded-xl border transition-colors duration-200",
      locked && "opacity-45",
      running && "border-amber-500/40 bg-amber-500/[0.04]",
      done && !running && "border-emerald-500/20 bg-emerald-500/[0.02]",
      !done && !running && !locked && "border-white/[0.07] bg-white/[0.02]",
    )}>
      <button
        className={cn(
          "flex w-full items-center gap-3 px-4 py-3 text-left transition-colors",
          hasBody && "hover:bg-white/[0.03]",
        )}
        onClick={() => hasBody && toggle(stageKey, defaultCollapsed)}
      >
        <PipelineNode num={num} done={done} running={running} />

        <span className="min-w-0 flex-1">
          <span className={cn(
            "block truncate text-sm font-semibold transition-colors",
            running ? "text-amber-400" : done ? "text-white/85" : "text-white/60",
          )}>
            {title}
          </span>
          {subtitle && (
            <span className="mt-0.5 block truncate text-[11px] text-white/35">{subtitle}</span>
          )}
        </span>

        {status}

        {hasBody && (
          <ChevronDown className={cn(
            "size-4 shrink-0 text-white/25 transition-transform duration-200",
            !open && "-rotate-90",
          )} />
        )}
      </button>

      {open && hasBody && (
        <div className="space-y-3 border-t border-white/[0.05] px-4 pb-4 pt-3">
          {children}
          {actions && (
            <div className="flex flex-wrap items-center gap-2 pt-1">{actions}</div>
          )}
        </div>
      )}
    </div>
  );
}
