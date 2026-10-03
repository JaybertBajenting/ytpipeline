import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import {
  api, type AppSettings, type Channel, type ChannelPublishUpdate, type Job, type SecretsUpdate,
} from "./api";
import { useUi } from "./store";

export const useChannels = () => useQuery({ queryKey: ["channels"], queryFn: api.channels });

export function useCreateChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, channel }: { id: string; channel: Partial<Channel> }) =>
      api.createChannel(id, channel),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["channels"] }),
  });
}

export function useDeleteChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.deleteChannel(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["channels"] });
      // Runs for the channel still exist on disk, so the list is unchanged — but the
      // channel filter options come from this query, so it has to refresh.
      qc.invalidateQueries({ queryKey: ["runs"] });
    },
  });
}

/** One channel's publish destinations. Separate query — tokens live in secrets.json. */
export const useChannelPublish = (id: string | null) =>
  useQuery({
    queryKey: ["channel-publish", id],
    queryFn: () => api.channelPublish(id!),
    enabled: !!id,
  });

export function useSaveChannelPublish(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (p: ChannelPublishUpdate) => api.saveChannelPublish(id, p),
    onSuccess: (view) => qc.setQueryData(["channel-publish", id], view),
  });
}

export function useScheduleRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ runId, scheduledUtc }: { runId: string; scheduledUtc: string | null }) =>
      api.scheduleRun(runId, scheduledUtc),
    onSuccess: (_m, { runId }) => {
      qc.invalidateQueries({ queryKey: ["run", runId] });
      qc.invalidateQueries({ queryKey: ["runs"] });
    },
  });
}

/**
 * Moves a video's files into Google Drive, or brings them back. Both run as jobs, so the card and the
 * detail view show progress and a Stop button exactly as they do for a stage.
 */
export function useDriveTransfer(runId: string) {
  const setJob = useUi((s) => s.setJob);
  return useMutation({
    mutationFn: (direction: "move" | "restore") =>
      direction === "move" ? api.moveToDrive(runId) : api.bringBackFromDrive(runId),
    onSuccess: ({ jobId }) => setJob(runId, jobId),
  });
}

/** Opens Explorer on this PC with the run's final.mp4 selected. */
export function useRevealVideo() {
  return useMutation({ mutationFn: (runId: string) => api.revealVideo(runId) });
}

export function useDeleteImage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ runId, name }: { runId: string; name: string }) => api.deleteImage(runId, name),
    // The detail query owns the frame list, so refetching it is what removes the tile.
    onSuccess: (_res, { runId }) => qc.invalidateQueries({ queryKey: ["run", runId] }),
  });
}

export function useDeleteRun() {
  const qc = useQueryClient();
  const select = useUi((s) => s.select);
  return useMutation({
    mutationFn: (runId: string) => api.deleteRun(runId),
    onSuccess: (_res, runId) => {
      // Drop the detail cache and bounce back to the grid — the run it showed is gone.
      qc.removeQueries({ queryKey: ["run", runId] });
      qc.invalidateQueries({ queryKey: ["runs"] });
      if (useUi.getState().selectedRunId === runId) select(null);
    },
  });
}

export function useSaveChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, channel }: { id: string; channel: Channel }) => api.saveChannel(id, channel),
    onSuccess: (saved) => {
      // Patch the list in place so the Channels grid updates without a refetch round-trip.
      qc.setQueryData<Channel[]>(["channels"], (prev) =>
        prev?.map((c) => (c.id === saved.id ? saved : c)) ?? prev);
    },
  });
}

export type BrandKind = "avatar" | "banner";

/**
 * Generating a channel's avatar or banner. Image work is slow, so the server runs it as a
 * background job; this polls that job and refreshes the channel list when it lands, which is what
 * flips hasAvatar/hasBanner and swaps the preview to the new png.
 */
export function useBrandImage(channelId: string) {
  const qc = useQueryClient();
  const [jobId, setJobId] = useState<string | null>(null);
  const [running, setRunning] = useState<BrandKind | null>(null);

  const jobQ = useQuery({
    queryKey: ["job", jobId],
    queryFn: () => api.job(jobId!),
    enabled: !!jobId,
    refetchInterval: (q) => (q.state.data?.status === "running" ? 1500 : false),
    retry: 30,
    retryDelay: 1500,
  });

  const status = jobQ.data?.status;
  useEffect(() => {
    if (!status || status === "running") return;
    qc.invalidateQueries({ queryKey: ["channels"] });
    setRunning(null);
    // The job id is kept so a failure's log stays on screen; only the busy flag clears.
  }, [status, qc]);

  const generate = useMutation({
    mutationFn: async (kind: BrandKind) => ({ ...(await api.generateBrand(channelId, kind)), kind }),
    onSuccess: ({ jobId: id, kind }) => { setJobId(id); setRunning(kind); },
  });

  const clear = useMutation({
    mutationFn: (kind: BrandKind) => api.clearBrand(channelId, kind),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["channels"] }),
  });

  return { generate, clear, job: jobQ.data as Job | undefined, running };
}

/**
 * Writes the channel's handle/byline/About/keywords with Claude. Deliberately NOT cached or
 * auto-saved: the result goes into the editor's draft so it can be read and edited, and only
 * reaches disk when the user presses Save.
 */
export function useGenerateIdentity(channelId: string) {
  return useMutation({ mutationFn: () => api.generateIdentity(channelId) });
}

export const useSecrets = () => useQuery({ queryKey: ["secrets"], queryFn: api.secrets });

export function useSaveSecrets() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: SecretsUpdate) => api.saveSecrets(s),
    onSuccess: (view) => qc.setQueryData(["secrets"], view),
  });
}

export const useRuns = () =>
  useQuery({ queryKey: ["runs"], queryFn: api.runs, refetchInterval: 5000 });

/**
 * Loads a run's detail. When `live` is true (a job is actively running) it polls every
 * 2s so completed stages light up immediately — critical for auto-run, where a single
 * job advances through script → voice → images → video and the manifest is updated
 * after each stage on disk.
 */
export const useRun = (id: string | null, live = false) =>
  useQuery({
    queryKey: ["run", id],
    queryFn: () => api.run(id!),
    enabled: !!id,
    // Poll whenever the SERVER says a job is running, not just when this browser started one.
    // Frame counts and stage state used to sit stale until a manual refresh if the work had been
    // kicked off from another tab, the CLI, or before a reload.
    refetchInterval: (q) => {
      if (q.state.data?.activeJob) return 2500;
      return live ? 2500 : false;
    },
  });

/**
 * Polls a job until it finishes. TanStack Query retries transient failures, so a brief
 * server hiccup during a long Codex run won't drop the watcher. On completion it refreshes
 * the run + run list.
 */
export function useJob(jobId: string | null, runId: string | null) {
  const qc = useQueryClient();
  const clearJob = useUi((s) => s.clearJob);

  const query = useQuery({
    queryKey: ["job", jobId],
    queryFn: () => api.job(jobId!),
    enabled: !!jobId,
    refetchInterval: (q) => (q.state.data?.status === "running" ? 1200 : false),
    retry: 30,
    retryDelay: 1500,
  });

  const status = query.data?.status;
  useEffect(() => {
    if (status && status !== "running") {
      if (runId) {
        qc.invalidateQueries({ queryKey: ["run", runId] });
        clearJob(runId);
      }
      qc.invalidateQueries({ queryKey: ["runs"] });
    }
  }, [status, runId, qc, clearJob]);

  return query.data as Job | undefined;
}

export function useGenerateScript() {
  const { setJob, select } = useUi();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ channel, topic, auto }: { channel: string; topic: string; auto: boolean }) =>
      api.generateScript(channel, topic, auto),
    onSuccess: ({ jobId, runId }) => {
      select(runId);
      setJob(runId, jobId);
      qc.invalidateQueries({ queryKey: ["runs"] });
    },
  });
}

export function useRunStage(runId: string) {
  const setJob = useUi((s) => s.setJob);
  return useMutation({
    mutationFn: ({ stage, privacy, force, music, provider, model }:
      { stage: any; privacy?: string; force?: boolean; music?: boolean; provider?: any; model?: string }) =>
      api.runStage(runId, stage, privacy, force, music, provider, model),
    onSuccess: ({ jobId }) => setJob(runId, jobId),
  });
}

export function useCancelJob() {
  const qc = useQueryClient();
  const { clearJob } = useUi();
  return useMutation({
    mutationFn: (jobId: string) => api.cancelJob(jobId),
    onSuccess: (_data, jobId) => {
      // Immediately stop polling this job and refresh runs
      qc.invalidateQueries({ queryKey: ["job", jobId] });
      qc.invalidateQueries({ queryKey: ["runs"] });
    },
  });
}

export const useSettings = () =>
  useQuery({ queryKey: ["settings"], queryFn: api.settings });

export function useSaveSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: AppSettings) => api.saveSettings(s),
    onSuccess: (saved) => qc.setQueryData(["settings"], saved),
  });
}

/** Which providers are quota-benched. Cheap (reads a small state file), so it can poll. */
export const useProviderHealth = () =>
  useQuery({ queryKey: ["provider-health"], queryFn: api.providerHealth, refetchInterval: 30000 });

/** Probes `gflow --version`. Manual trigger only — it shells out, so we don't poll it. */
export function useCheckGflow() {
  return useMutation({ mutationFn: () => api.checkGflow() });
}

export function useApprove(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (stage: "script" | "video") => api.approve(runId, stage),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["run", runId] });
      qc.invalidateQueries({ queryKey: ["runs"] });
    },
  });
}
