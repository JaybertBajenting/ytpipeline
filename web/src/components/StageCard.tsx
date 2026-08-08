import { ChevronDown } from "lucide-react";
import type { ReactNode } from "react";
import { useUi } from "@/lib/store";
import { PipelineNode } from "./ui";
import { cn } from "@/lib/utils";

export function StageCard({
  stageKey, num, title, done, locked, running, status, children, actions,
}: {
  stageKey: string;
  num: ReactNode;
  title: string;
  done?: boolean;
  locked?: boolean;
  running?: boolean;
  status?: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
}) {
  const collapsed = useUi((s) => s.collapsed.has(stageKey));
  const toggle = useUi((s) => s.toggleCollapsed);

  return (
    <div className={cn(
      "rounded-xl border transition-all duration-200",
      locked   && "opacity-40",
      running  && "border-amber-500/40 bg-amber-500/[0.04] shadow-lg shadow-amber-500/5",
      done && !running && "border-emerald-500/20 bg-emerald-500/[0.03]",
      !done && !running && !locked && "border-white/[0.07] bg-white/[0.02]",
    )}>
      <button
        className="flex w-full items-center gap-3 px-4 py-3 text-left"
        onClick={() => toggle(stageKey)}
      >
        <PipelineNode num={num} done={done} running={running} />

        <span className={cn(
          "flex-1 text-sm font-semibold transition-colors",
          running ? "text-amber-400" : done ? "text-white/85" : "text-white/60"
        )}>
          {title}
        </span>

        {status}

        <ChevronDown className={cn(
          "size-4 text-white/25 transition-transform duration-200",
          collapsed && "-rotate-90"
        )} />
      </button>

      {!collapsed && (children || actions) && (
        <div className="space-y-3 px-4 pb-4 pt-1">
          {children}
          {actions && (
            <div className="flex flex-wrap items-center gap-2 pt-1">
              {actions}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
