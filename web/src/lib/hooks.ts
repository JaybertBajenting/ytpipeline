import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
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
    refetchInterval: live ? 2000 : false,
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
    mutationFn: ({ stage, privacy, force }: { stage: any; privacy?: string; force?: boolean }) =>
      api.runStage(runId, stage, privacy, force),
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
