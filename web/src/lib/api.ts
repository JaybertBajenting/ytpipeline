import axios from "axios";

export const http = axios.create({ baseURL: "" });

// ---- Channels --------------------------------------------------------------

export interface ChannelVoice {
  /** Empty = inherit ELEVENLABS_VOICE_ID. */
  voiceId: string;
  /** Friendly label — ElevenLabs ids are opaque. */
  voiceName: string;
  /** Empty = inherit the global model. */
  modelId: string;
  /** null = inherit the global value. */
  stability: number | null;
  similarityBoost: number | null;
}

export interface YoutubeDefaults {
  categoryId: string;
  defaultTags: string[];
  titlePattern: string;
  descriptionFooter: string;
}

export interface Channel {
  id: string;
  name: string;
  tagline: string;
  persona: string;
  audience: string;
  tone: string;
  /** Legacy single value, kept in sync with the range's lower bound. */
  videoLengthTargetMinutes: number;
  videoLengthMinMinutes: number;
  videoLengthMaxMinutes: number;
  scriptStructure: string[];
  visualStyle: string;
  voiceNotes: string;
  voice: ChannelVoice;
  music: boolean;
  youtube: YoutubeDefaults;
  /** True only when the channel has its OWN <id>.image.md — not when inheriting the default. */
  hasImageRule: boolean;
  /** Which rule is actually in force: its own file, the shared default, or nothing. */
  imageRuleSource: "channel" | "default" | "none";
}

// ---- Runs ------------------------------------------------------------------

export interface StageStatus {
  done: boolean;
  path?: string;
  completedUtc?: string;
  count?: number;
  seconds?: number;
  musicTracks?: string[];
  /** Publish targets: platform post id, permalink, and why a publish did nothing. */
  postId?: string;
  url?: string;
  skipReason?: string;
  videoId?: string;
  privacy?: string;
}

export interface Manifest {
  runId: string;
  channel: string;
  topic: string;
  createdUtc: string;
  stages: {
    script: StageStatus;
    voice: StageStatus;
    images: StageStatus;
    video: StageStatus;
    /** YouTube. Named `upload` for compatibility with manifests written before the split. */
    upload: StageStatus;
    facebook: StageStatus;
    instagram: StageStatus;
  };
  review: { scriptApproved: boolean; videoApproved: boolean };
  publish: {
    title: string;
    description: string;
    tags: string[];
    hasThumbnail: boolean;
    /** Planned publish date. A plan only — nothing publishes on a timer. */
    scheduledUtc: string | null;
  };
}

export interface Beat { index: number; narration: string; imagePrompt: string }
export interface Script {
  title: string;
  description: string;
  tags: string[];
  beats: Beat[];
  fullNarration: string;
}

export interface RunDetail {
  manifest: Manifest;
  script: Script | null;
  images: string[];
  hasAudio: boolean;
  hasVideo: boolean;
  hasThumbnail: boolean;
  /** Files and bytes in the run folder — shown in the delete confirmation. */
  fileCount: number;
  sizeBytes: number;
  /**
   * A job running for this run, reported by the server. Non-null regardless of which tab (or the
   * CLI) started it, which is what lets the UI stay live without a manual refresh.
   */
  activeJob: { id: string; stage: string } | null;
}

export type JobStatus = "running" | "succeeded" | "failed";
export interface Job {
  id: string;
  runId: string;
  stage: string;
  status: JobStatus;
  error?: string | null;
  logCount: number;
  logs: string[];
}

export type StageName =
  | "script" | "voice" | "images" | "video" | "thumbnail"
  | "upload" | "facebook" | "instagram" | "publish-all";

/** Publish destinations a card can trigger, in the order "Upload all" runs them. */
export const PUBLISH_TARGETS = [
  { stage: "upload"    as const, key: "upload"    as const, label: "YouTube" },
  { stage: "facebook"  as const, key: "facebook"  as const, label: "Facebook" },
  { stage: "instagram" as const, key: "instagram" as const, label: "Instagram" },
];

// ---- Settings --------------------------------------------------------------

export type ImageProvider = "codex" | "gflow";

export interface GflowSettings {
  executable: string;
  argsTemplate: string[];
  outputFlag: string;
  checkArgs: string[];
  aspect: string;
  model: string;
  project: string;
  extraArgs: string[];
  timeoutSeconds: number;
  maxConcurrent: number;
}

export interface AppSettings {
  imageProvider: ImageProvider;
  /** Provider that finishes the job if the primary stops short. "" = disabled. */
  imageFallbackProvider: ImageProvider | "";
  gflow: GflowSettings;
  voiceGain: number;
  musicEnabled: boolean;
  musicVolume: number;
}

/** Whether a provider is currently benched for quota, and when it gets retried. */
export interface ProviderHealth {
  provider: ImageProvider;
  preferred: boolean;
  standby: boolean;
  benched: boolean;
  benchedUntilUtc: string | null;
  retryInMinutes: number | null;
  exhaustedUtc: string | null;
  consecutiveProbeFailures: number;
  lastError: string;
}

export interface GflowCheck {
  installed: boolean;
  version: string | null;
  error: string | null;
}

// ---- Publishing credentials -------------------------------------------------
// GET returns ids in the clear and every real secret as a `*Saved` boolean — the values
// themselves never leave the server. On PUT, leaving a secret field blank keeps the stored
// value, so saving the form can't wipe a working token.

/**
 * App-level credentials only — the developer app identity, shared by every channel.
 * Where a video actually goes (and the token for that destination) is per-channel:
 * see {@link ChannelPublishView}.
 */
export interface SecretsView {
  youtube: {
    enabled: boolean;
    clientId: string;
    defaultPrivacy: string;
    clientSecretSaved: boolean;
  };
  facebook: { enabled: boolean; appId: string; appSecretSaved: boolean };
  instagram: { enabled: boolean; appId: string; appSecretSaved: boolean };
}

/** One channel's publish destinations. Tokens are reported as `*Saved` flags only. */
export interface ChannelPublishView {
  youtubeChannelId: string;
  facebookPageId: string;
  instagramUserId: string;
  publicVideoBaseUrl: string;
  facebookPageAccessTokenSaved: boolean;
  instagramAccessTokenSaved: boolean;
}

/** PUT body for a channel's destinations. Tokens follow the null / "" / value rules. */
export interface ChannelPublishUpdate {
  youtubeChannelId: string;
  facebookPageId: string;
  instagramUserId: string;
  publicVideoBaseUrl: string;
  facebookPageAccessToken: string | null;
  instagramAccessToken: string | null;
}

/**
 * Shape sent on PUT. Secret fields are `string | null`, and the three states are distinct:
 * `null` = leave the stored value alone (an untouched field), `""` = clear it, anything
 * else = replace it. Without the null/"" split there'd be no way to remove a credential.
 */
export interface SecretsUpdate {
  youtube: {
    enabled: boolean;
    clientId: string;
    clientSecret: string | null;
    defaultPrivacy: string;
  };
  facebook: { enabled: boolean; appId: string; appSecret: string | null };
  instagram: { enabled: boolean; appId: string; appSecret: string | null };
}

// ---- API calls -------------------------------------------------------------
export interface DeleteChannelResult {
  deleted: string;
  imageRuleKept: boolean;
  imageRulePath: string | null;
  publishTargetsRemoved: boolean;
  orphanedRuns: number;
}

export const api = {
  channels: () => http.get<Channel[]>("/api/channels").then((r) => r.data),
  channel: (id: string) => http.get<Channel>(`/api/channels/${id}`).then((r) => r.data),
  saveChannel: (id: string, c: Channel) =>
    http.put<Channel>(`/api/channels/${id}`, c).then((r) => r.data),
  createChannel: (id: string, channel: Partial<Channel>) =>
    http.post<Channel>("/api/channels", { id, channel }).then((r) => r.data),
  deleteChannel: (id: string) =>
    http.delete<DeleteChannelResult>(`/api/channels/${id}`).then((r) => r.data),

  channelPublish: (id: string) =>
    http.get<ChannelPublishView>(`/api/channels/${id}/publish`).then((r) => r.data),
  saveChannelPublish: (id: string, p: ChannelPublishUpdate) =>
    http.put<ChannelPublishView>(`/api/channels/${id}/publish`, p).then((r) => r.data),

  runs: () => http.get<Manifest[]>("/api/runs").then((r) => r.data),
  run: (id: string) => http.get<RunDetail>(`/api/runs/${id}`).then((r) => r.data),
  deleteRun: (id: string) =>
    http.delete<{ deleted: string; files: number; sizeBytes: number }>(`/api/runs/${id}`)
      .then((r) => r.data),
  scheduleRun: (id: string, scheduledUtc: string | null) =>
    http.put<Manifest>(`/api/runs/${id}/schedule`, { scheduledUtc }).then((r) => r.data),
  job: (id: string) => http.get<Job>(`/api/jobs/${id}?since=0`).then((r) => r.data),

  generateScript: (channel: string, topic: string, auto: boolean) =>
    http.post<{ jobId: string; runId: string }>("/api/script", { channel, topic, auto }).then((r) => r.data),

  runStage: (runId: string, stage: StageName, privacy?: string, force?: boolean) => {
    const qs = new URLSearchParams();
    if (privacy) qs.set("privacy", privacy);
    if (force) qs.set("force", "true");
    const suffix = qs.toString() ? `?${qs}` : "";
    return http.post<{ jobId: string }>(`/api/runs/${runId}/stage/${stage}${suffix}`).then((r) => r.data);
  },

  approve: (runId: string, stage: "script" | "video") =>
    http.post(`/api/runs/${runId}/approve`, { stage }).then((r) => r.data),

  cancelJob: (jobId: string) => http.delete(`/api/jobs/${jobId}`).then((r) => r.data),

  settings: () => http.get<AppSettings>("/api/settings").then((r) => r.data),
  saveSettings: (s: AppSettings) => http.put<AppSettings>("/api/settings", s).then((r) => r.data),
  checkGflow: () => http.get<GflowCheck>("/api/settings/check-gflow").then((r) => r.data),
  providerHealth: () => http.get<ProviderHealth[]>("/api/provider-health").then((r) => r.data),

  secrets: () => http.get<SecretsView>("/api/secrets").then((r) => r.data),
  saveSecrets: (s: SecretsUpdate) => http.put<SecretsView>("/api/secrets", s).then((r) => r.data),

  // File URLs (cache-busted where the file can change)
  imageUrl: (runId: string, name: string) => `/api/runs/${runId}/image/${encodeURIComponent(name)}`,
  audioUrl: (runId: string) => `/api/runs/${runId}/audio`,
  videoUrl: (runId: string) => `/api/runs/${runId}/video`,
  thumbnailUrl: (runId: string, bust = 0) => `/api/runs/${runId}/thumbnail${bust ? `?t=${bust}` : ""}`,
};

// ---- Derived helpers --------------------------------------------------------

export const STAGE_ORDER = ["script", "voice", "images", "video", "upload"] as const;

/** How many of the five stages are done — drives the progress meter on video cards. */
export function stagesDone(m: Manifest): number {
  return STAGE_ORDER.filter((k) => m.stages[k].done).length;
}

/** The first stage that hasn't completed, or null when the run is fully finished. */
export function nextStage(m: Manifest): string | null {
  return STAGE_ORDER.find((k) => !m.stages[k].done) ?? null;
}

/** Words-per-minute the script stage assumes. Mirrors ClaudeCliScriptGenerator. */
export const WPM_LOW = 130;
export const WPM_HIGH = 150;

/** What a channel's length range costs, in the terms that matter: words and images. */
export function lengthBudget(minMin: number, maxMin: number) {
  const lo = Math.max(1, minMin || 1);
  const hi = Math.max(lo, maxMin || lo);
  const words = { min: lo * WPM_LOW, max: hi * WPM_HIGH };
  return {
    lo, hi,
    isRange: hi > lo,
    minutesLabel: hi > lo ? `${lo}-${hi} min` : `${lo} min`,
    words,
    wordsLabel: `${words.min}–${words.max}`,
    beats: { min: Math.round(words.min / 6), max: Math.round(words.max / 6) },
  };
}

export function formatBytes(bytes: number): string {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

/** Starting point for a new channel — a usable shape, not an empty form. */
export function newChannelTemplate(name: string): Partial<Channel> {
  return {
    name,
    tagline: "",
    persona: "",
    audience: "",
    tone: "",
    videoLengthTargetMinutes: 3,
    videoLengthMinMinutes: 3,
    videoLengthMaxMinutes: 5,
    scriptStructure: ["Cold open hook", "Plain-language answer", "Mechanism in 3-4 beats", "Recap + outro"],
    visualStyle: "",
    voiceNotes: "",
    voice: { voiceId: "", voiceName: "", modelId: "", stability: null, similarityBoost: null },
    music: true,
    youtube: { categoryId: "27", defaultTags: [], titlePattern: "{topic}", descriptionFooter: "" },
  };
}
