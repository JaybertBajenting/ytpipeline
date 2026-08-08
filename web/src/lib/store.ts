import { create } from "zustand";

/** Top-level destinations in the sidebar. */
export type View = "dashboard" | "videos" | "channels" | "settings";

interface UiState {
  /** Which sidebar destination is showing. */
  view: View;
  /** Set when a video card is opened — the Videos view then shows the detail. */
  selectedRunId: string | null;
  /** Channel id being edited on the Channels view. */
  selectedChannelId: string | null;
  /** True while the "New video" composer dialog is open. */
  composerOpen: boolean;
  /** Active job id per run, so multiple runs can progress concurrently. */
  jobs: Record<string, string>;
  /** Stage keys the user collapsed (persists across re-renders). */
  collapsed: Set<string>;
  /** Sidebar collapsed to icons only. */
  railCollapsed: boolean;

  go: (view: View) => void;
  select: (runId: string | null) => void;
  selectChannel: (id: string | null) => void;
  setComposerOpen: (open: boolean) => void;
  setJob: (runId: string, jobId: string) => void;
  clearJob: (runId: string) => void;
  toggleCollapsed: (key: string) => void;
  toggleRail: () => void;
}

export const useUi = create<UiState>((set) => ({
  view: "dashboard",
  selectedRunId: null,
  selectedChannelId: null,
  composerOpen: false,
  jobs: {},
  collapsed: new Set<string>(),
  railCollapsed: false,

  // Navigating away from a video clears the selection, so returning to Videos lands on
  // the grid rather than silently reopening whatever was last read.
  go: (view) => set((s) => ({ view, selectedRunId: view === "videos" ? s.selectedRunId : null })),

  // Selecting a run always lands you on the Videos view showing that run.
  select: (runId) => set({ selectedRunId: runId, view: "videos" }),
  selectChannel: (id) => set({ selectedChannelId: id }),
  setComposerOpen: (composerOpen) => set({ composerOpen }),

  setJob: (runId, jobId) => set((s) => ({ jobs: { ...s.jobs, [runId]: jobId } })),
  clearJob: (runId) =>
    set((s) => {
      const next = { ...s.jobs };
      delete next[runId];
      return { jobs: next };
    }),

  toggleCollapsed: (key) =>
    set((s) => {
      const next = new Set(s.collapsed);
      next.has(key) ? next.delete(key) : next.add(key);
      return { collapsed: next };
    }),

  toggleRail: () => set((s) => ({ railCollapsed: !s.railCollapsed })),
}));
