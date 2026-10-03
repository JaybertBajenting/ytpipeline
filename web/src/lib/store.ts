import { create } from "zustand";
import { createJSONStorage, persist } from "zustand/middleware";

/** Top-level destinations in the sidebar. */
export type View = "dashboard" | "videos" | "calendar" | "channels" | "settings";

/** Status filter on the Videos grid. */
export type VideoStatusFilter = "all" | "in-progress" | "ready" | "uploaded";

const VIDEO_STATUS_FILTERS: readonly VideoStatusFilter[] = ["all", "in-progress", "ready", "uploaded"];

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
  /**
   * Explicit user overrides for stage-card open state. Two sets rather than one because each card
   * has its own sensible default (finished stages start closed, the actionable one starts open),
   * and a single "collapsed" set couldn't express "the user deliberately opened this".
   */
  collapsed: Set<string>;
  expanded: Set<string>;
  /** Sidebar collapsed to icons only. */
  railCollapsed: boolean;
  /**
   * Videos grid filters. Held here rather than in the page because the page unmounts whenever
   * another sidebar destination opens. Channel and status are also saved across reloads.
   */
  videoChannel: string;
  videoStatus: VideoStatusFilter;
  videoQuery: string;

  go: (view: View) => void;
  select: (runId: string | null) => void;
  selectChannel: (id: string | null) => void;
  setComposerOpen: (open: boolean) => void;
  setJob: (runId: string, jobId: string) => void;
  clearJob: (runId: string) => void;
  /** Flips a card, given what it would do by default when the user hasn't chosen. */
  toggleCollapsed: (key: string, defaultCollapsed?: boolean) => void;
  toggleRail: () => void;
  setVideoChannel: (channel: string) => void;
  setVideoStatus: (status: VideoStatusFilter) => void;
  setVideoQuery: (query: string) => void;
  clearVideoFilters: () => void;
}

export const useUi = create<UiState>()(
  persist(
    (set) => ({
      view: "dashboard",
      selectedRunId: null,
      selectedChannelId: null,
      composerOpen: false,
      jobs: {},
      collapsed: new Set<string>(),
      expanded: new Set<string>(),
      railCollapsed: false,
      videoChannel: "all",
      videoStatus: "all",
      videoQuery: "",

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

      toggleCollapsed: (key, defaultCollapsed = false) =>
        set((s) => {
          const isOpen = s.expanded.has(key)
            ? true
            : s.collapsed.has(key) ? false : !defaultCollapsed;

          const collapsed = new Set(s.collapsed);
          const expanded = new Set(s.expanded);
          // Record the new state in exactly one set so the override is unambiguous.
          if (isOpen) { expanded.delete(key); collapsed.add(key); }
          else { collapsed.delete(key); expanded.add(key); }
          return { collapsed, expanded };
        }),

      toggleRail: () => set((s) => ({ railCollapsed: !s.railCollapsed })),

      setVideoChannel: (videoChannel) => set({ videoChannel }),
      setVideoStatus: (videoStatus) => set({ videoStatus }),
      setVideoQuery: (videoQuery) => set({ videoQuery }),
      clearVideoFilters: () => set({ videoChannel: "all", videoStatus: "all", videoQuery: "" }),
    }),
    {
      name: "ytpipeline-ui",
      storage: createJSONStorage(() => localStorage),
      // Save the grid filters and nothing else. The search box is left out on purpose — a query
      // still sitting there after a relaunch reads as "my videos are gone". The rest is
      // per-session: Sets don't serialise, and a saved job id or open run would point at stale work.
      partialize: (s) => ({ videoChannel: s.videoChannel, videoStatus: s.videoStatus }),
      // A value saved by an older build, or a hand-edited key, must not strand the grid on a
      // status that no longer exists.
      merge: (saved, current) => {
        const s = (saved ?? {}) as Partial<UiState>;
        return {
          ...current,
          videoChannel: typeof s.videoChannel === "string" ? s.videoChannel : current.videoChannel,
          videoStatus: VIDEO_STATUS_FILTERS.includes(s.videoStatus as VideoStatusFilter)
            ? (s.videoStatus as VideoStatusFilter)
            : current.videoStatus,
        };
      },
    },
  ),
);
