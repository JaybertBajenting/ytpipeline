import {
  CalendarDays, ChevronsLeft, Film, LayoutDashboard, Layers, Plus,
  Settings as SettingsIcon, Tv,
} from "lucide-react";
import type { ReactNode } from "react";
import { useRuns } from "@/lib/hooks";
import { useUi, type View } from "@/lib/store";
import { Button, Spinner } from "./ui";
import { cn } from "@/lib/utils";

const NAV: { id: View; label: string; icon: ReactNode; hint: string }[] = [
  { id: "dashboard", label: "Dashboard", icon: <LayoutDashboard className="size-4" />, hint: "Overview" },
  { id: "videos",    label: "Videos",    icon: <Film className="size-4" />,            hint: "All runs" },
  { id: "calendar",  label: "Calendar",  icon: <CalendarDays className="size-4" />,    hint: "Publish plan" },
  { id: "channels",  label: "Channels",  icon: <Tv className="size-4" />,              hint: "Per-channel setup" },
  { id: "settings",  label: "Settings",  icon: <SettingsIcon className="size-4" />,    hint: "Providers & keys" },
];

export function Sidebar() {
  const view = useUi((s) => s.view);
  const go = useUi((s) => s.go);
  const collapsed = useUi((s) => s.railCollapsed);
  const toggleRail = useUi((s) => s.toggleRail);
  const jobs = useUi((s) => s.jobs);
  const setComposerOpen = useUi((s) => s.setComposerOpen);
  const { data: runs } = useRuns();

  const activeJobs = Object.keys(jobs).length;

  return (
    <aside className={cn(
      "flex shrink-0 flex-col border-r border-white/[0.06] bg-[#0d0d0f] transition-[width] duration-200",
      collapsed ? "w-[60px]" : "w-[212px]",
    )}>
      {/* ── Brand ─────────────────────────────────────────────────────────── */}
      <div className="flex h-14 items-center gap-2.5 px-3">
        <div className="grid size-8 shrink-0 place-items-center rounded-lg bg-amber-500">
          <Layers className="size-4 text-black" strokeWidth={2.5} />
        </div>
        {!collapsed && (
          <div className="min-w-0 flex-1">
            <div className="truncate text-[13px] font-semibold leading-tight tracking-tight text-white">
              YtPipeline
            </div>
            <div className="truncate text-[10px] text-white/30">YouTube automation</div>
          </div>
        )}
      </div>

      {/* ── New video ─────────────────────────────────────────────────────── */}
      <div className="px-3 pb-3">
        <Button
          variant="primary"
          size={collapsed ? "icon" : "block"}
          onClick={() => setComposerOpen(true)}
          title="New video"
        >
          <Plus className="size-4" strokeWidth={2.5} />
          {!collapsed && "New video"}
        </Button>
      </div>

      {/* ── Nav ───────────────────────────────────────────────────────────── */}
      <nav className="flex flex-1 flex-col gap-0.5 px-2">
        {NAV.map((item) => {
          const active = view === item.id;
          const count = item.id === "videos" ? runs?.length : undefined;
          return (
            <button
              key={item.id}
              onClick={() => go(item.id)}
              title={collapsed ? item.label : undefined}
              className={cn(
                "group flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-[13px] font-medium transition-colors",
                active
                  ? "bg-amber-500/[0.12] text-amber-400"
                  : "text-white/45 hover:bg-white/[0.05] hover:text-white/80",
                collapsed && "justify-center px-0",
              )}
            >
              <span className={cn("shrink-0", active ? "text-amber-400" : "text-white/40 group-hover:text-white/70")}>
                {item.icon}
              </span>
              {!collapsed && (
                <>
                  <span className="flex-1 truncate">{item.label}</span>
                  {count !== undefined && count > 0 && (
                    <span className="rounded-full bg-white/[0.08] px-1.5 text-[10px] tabular-nums text-white/40">
                      {count}
                    </span>
                  )}
                </>
              )}
            </button>
          );
        })}
      </nav>

      {/* ── Footer ────────────────────────────────────────────────────────── */}
      <div className="border-t border-white/[0.06] p-2">
        {activeJobs > 0 && (
          <div className={cn(
            "mb-1.5 flex items-center gap-2 rounded-lg border border-amber-500/25 bg-amber-500/[0.07] px-2.5 py-1.5 text-[11px] text-amber-400",
            collapsed && "justify-center px-0",
          )}>
            <Spinner className="size-3 shrink-0" />
            {!collapsed && <span>{activeJobs} job{activeJobs > 1 ? "s" : ""} running</span>}
          </div>
        )}
        <button
          onClick={toggleRail}
          className={cn(
            "flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-[11px] text-white/30 transition-colors hover:bg-white/[0.05] hover:text-white/60",
            collapsed && "justify-center px-0",
          )}
        >
          <ChevronsLeft className={cn("size-3.5 shrink-0 transition-transform", collapsed && "rotate-180")} />
          {!collapsed && "Collapse"}
        </button>
      </div>
    </aside>
  );
}
