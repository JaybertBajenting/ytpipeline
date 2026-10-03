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

export interface ChannelLink {
  label: string;
  url: string;
}

/**
 * How the CHANNEL presents on YouTube — separate from persona/tone, which describe how the videos
 * are written. Recorded for you to copy into Studio; nothing is pushed automatically.
 */
export interface ChannelIdentity {
  /** Without the leading @. */
  handle: string;
  /** The About-page text. YouTube caps this at 1000 characters. */
  bio: string;
  /** One-liner under the channel name; falls back to the tagline when empty. */
  byline: string;
  keywords: string[];
  links: ChannelLink[];
  /** Optional extra art direction, layered on the channel's image rule. */
  avatarPrompt: string;
  bannerPrompt: string;
  /** Wording to letter across the banner; empty draws it wordless. Edited in the channel json. */
  bannerHeadline: string;
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
  /** House format for the YouTube description. Empty = a few plain sentences, as before. */
  descriptionFormat: string;
  /** Seconds each image holds. 0 = the default ~2s fast cut. Sets beat length, so it sets image count. */
  secondsPerImage: number;
  visualStyle: string;
  /** Backend that draws this channel. "" follows Settings; a named one never falls back to the other. */
  imageProvider: ImageProvider | "";
  /** Real source images/transcripts studied on every generation. Not edited here, but must round-trip. */
  referenceStudy?: {
    sourceUrl: string; transcriptPaths: string[]; imagePaths: string[]; thumbnailPaths: string[];
  } | null;
  voiceNotes: string;
  voice: ChannelVoice;
  music: boolean;
  youtube: YoutubeDefaults;
  identity: ChannelIdentity;
  /** True only when the channel has its OWN <id>.image.md — not when inheriting the default. */
  hasImageRule: boolean;
  /** Which rule is actually in force: its own file, the shared default, or nothing. */
  imageRuleSource: "channel" | "default" | "none";
  /** Read-only: the branding pngs are files beside the channel json, not fields in it. */
  hasAvatar: boolean;
  hasBanner: boolean;
}

// ---- Runs ------------------------------------------------------------------

export interface StageStatus {
  done: boolean;
  path?: string;
  completedUtc?: string;
  count?: number;
  seconds?: number;
  musicTracks?: string[];
  musicCredits?: string[];
  /**
   * Video stage: this run's background-music choice, remembered across re-assemblies.
   * Undefined means "follow the channel setting" — it's only written once you pick.
   */
  music?: boolean;
  /** Voice stage: the ElevenLabs model that voiced the mp3 on disk. */
  model?: string;
  /**
   * Voice stage: a model picked for this video alone, kept for its next voiceover. Undefined means
   * it follows the channel.
   */
  modelChoice?: string;
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
  /** Set once the files were moved to Google Drive; only the manifest and thumbnail stay on this PC. */
  archive?: {
    /** Where it shows in File Explorer via Google Drive for desktop; "" if that folder isn't set. */
    location: string;
    /** rclone remote path when uploaded with rclone; null when copied into the Drive folder. */
    remote?: string | null;
    movedUtc: string; files: number; bytes: number;
  } | null;
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
  | "script" | "voice" | "images" | "video" | "thumbnail" | "description"
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
  /** Backend for the publish thumbnail. Empty follows imageProvider. */
  thumbnailProvider: ImageProvider | "";
  /** Codex coordinator settings for frames, thumbnails, and channel art. */
  codexModel: "gpt-6.1-sol" | "gpt-6-astra" | "gpt-6-sol" | "gpt-6-luna" | "gpt-5.6-sol" | "gpt-5.6-terra" | "gpt-5.6-luna" | "gpt-5.5";
  codexReasoningEffort: "low" | "medium" | "high" | "xhigh" | "max" | "ultra";
  gflow: GflowSettings;
  /** ElevenLabs model for channels that don't pick one. "" = appsettings.json's (eleven_multilingual_v2). */
  elevenLabsModel: string;
  voiceGain: number;
  musicEnabled: boolean;
  musicVolume: number;
  /** Where Google Drive for desktop shows these files; also the move target when no rclone remote is set. */
  driveArchiveFolder: string;
  /** rclone destination, e.g. "gdrive:YtPipeline". Set = upload straight to Google, freeing space at once. */
  rcloneRemote: string;
  rcloneExecutable: string;
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
  /** The CLI ran. Says nothing about whether it can generate — see `connected`. */
  installed: boolean;
  /**
   * The backend is up AND the browser extension is attached, i.e. generation will actually work.
   * Separate from `installed` because `flow status` exits 0 while reporting a disconnected
   * extension, so the exit code alone reported "reachable" through a run where every beat failed.
   */
  connected: boolean;
  /** The probe's own `status:` line, e.g. "unauthorized_or_disconnected". */
  state: string;
  /** What to do about it, when the CLI ran but cannot generate. */
  remedy: string | null;
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

  runStage: (
    runId: string, stage: StageName, privacy?: string, force?: boolean, music?: boolean,
    provider?: ImageProvider, model?: string,
  ) => {
    const qs = new URLSearchParams();
    if (privacy) qs.set("privacy", privacy);
    if (force) qs.set("force", "true");
    // Images only: pins the whole set to one backend. They don't draw alike, so a set finished on
    // the other one is visible in the video.
    if (provider) qs.set("provider", provider);
    // Voice only: an ElevenLabs model id for this video, or "default" for the channel's.
    if (model) qs.set("model", model);
    // Explicit false is meaningful here, so this tests for undefined rather than falsiness.
    if (music !== undefined) qs.set("music", String(music));
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

  // File URLs (cache-busted where the file can change).
  //
  // voice.mp3 and final.mp4 are overwritten in place every time you re-render or re-assemble, so
  // the URL alone is not enough: with the page already open the browser keeps serving whatever it
  // buffered for that same src and the player shows the OLD file — a re-assembled 3:32 video
  // stuck reading as 10 seconds. Pass the stage's completedUtc, which changes exactly when the
  // file is rewritten. (Deliberately not the query's dataUpdatedAt: that ticks on every poll and
  // would reload the player mid-playback.)
  // Suggests handle/byline/bio/keywords from the channel's persona. Returns the text without
  // saving, so the editor can drop it into the draft for review. Slow — it shells out to Claude.
  generateIdentity: (channelId: string) =>
    http.post<ChannelIdentity>(`/api/channels/${channelId}/identity/generate`).then((r) => r.data),

  // Channel branding art. Cache-busted for the same reason the run media is: the png is
  // overwritten in place by a regenerate, so a fixed URL would keep showing the old art.
  brandUrl: (channelId: string, kind: "avatar" | "banner", bust?: string | number) =>
    `/api/channels/${channelId}/brand/${kind}${bust ? `?t=${encodeURIComponent(String(bust))}` : ""}`,
  generateBrand: (channelId: string, kind: "avatar" | "banner") =>
    http.post<{ jobId: string }>(`/api/channels/${channelId}/brand/${kind}`).then((r) => r.data),
  clearBrand: (channelId: string, kind: "avatar" | "banner") =>
    http.delete(`/api/channels/${channelId}/brand/${kind}`).then((r) => r.data),

  imageUrl: (runId: string, name: string) => `/api/runs/${runId}/image/${encodeURIComponent(name)}`,
  deleteImage: (runId: string, name: string) =>
    http.delete<{ deleted: string }>(`/api/runs/${runId}/image/${encodeURIComponent(name)}`)
      .then((r) => r.data),
  audioUrl: (runId: string, bust?: string | null) =>
    `/api/runs/${runId}/audio${bust ? `?t=${encodeURIComponent(bust)}` : ""}`,
  moveToDrive: (runId: string) =>
    http.post<{ jobId: string }>(`/api/runs/${runId}/drive/move`).then((r) => r.data),
  bringBackFromDrive: (runId: string) =>
    http.post<{ jobId: string }>(`/api/runs/${runId}/drive/restore`).then((r) => r.data),
  revealVideo: (runId: string) =>
    http.post<{ opened: string }>(`/api/runs/${runId}/reveal`).then((r) => r.data),
  videoUrl: (runId: string, bust?: string | null) =>
    `/api/runs/${runId}/video${bust ? `?t=${encodeURIComponent(bust)}` : ""}`,
  thumbnailUrl: (runId: string, bust: string | number = 0) =>
    `/api/runs/${runId}/thumbnail${bust ? `?t=${encodeURIComponent(bust)}` : ""}`,
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

/**
 * A blank identity. Channel files written before this section existed have no `identity` key at
 * all, so the editor substitutes this rather than reading through undefined.
 */
export function emptyIdentity(): ChannelIdentity {
  return {
    handle: "", bio: "", byline: "", keywords: [], links: [],
    avatarPrompt: "", bannerPrompt: "", bannerHeadline: "",
  };
}

/** YouTube's own limit on the About text — surfaced so the field can warn before Studio rejects it. */
export const BIO_MAX_CHARS = 1000;

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
    descriptionFormat: "",
    secondsPerImage: 0,
    visualStyle: "",
    imageProvider: "",
    voiceNotes: "",
    voice: { voiceId: "", voiceName: "", modelId: "", stability: null, similarityBoost: null },
    music: true,
    youtube: { categoryId: "27", defaultTags: [], titlePattern: "{topic}", descriptionFooter: "" },
    identity: emptyIdentity(),
  };
}
