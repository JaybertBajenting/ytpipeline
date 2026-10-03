import type { AppSettings, Channel } from "./api";

/**
 * ElevenLabs models offered in the pickers. The API key here can't list models (it lacks
 * models_read), so this is kept by hand; a model saved before it left the list still shows, see
 * voiceModelOptions. Character limits per request live server-side, in RequestCharacterLimit.
 */
export const VOICE_MODELS: readonly { id: string; label: string; note: string }[] = [
  { id: "eleven_v4", label: "Eleven v4", note: "newest, most expressive" },
  { id: "eleven_v4_turbo", label: "Eleven v4 Turbo", note: "faster v4" },
  { id: "eleven_v3", label: "Eleven v3", note: "expressive, reads [sighs]-style tags" },
  { id: "eleven_multilingual_v2", label: "Multilingual v2", note: "steady studio read" },
  { id: "eleven_flash_v2_5", label: "Flash v2.5", note: "fastest, cheapest" },
];

/** appsettings.json's ElevenLabs:ModelId, used when Settings leaves the default empty. */
export const BUILT_IN_VOICE_MODEL = "eleven_multilingual_v2";

export function voiceModelLabel(id: string | undefined | null) {
  if (!id) return "";
  return VOICE_MODELS.find((m) => m.id === id)?.label ?? id;
}

/** The known models, plus `current` when it's one this list doesn't know, so a select never shows blank. */
export function voiceModelOptions(current?: string | null) {
  return current && !VOICE_MODELS.some((m) => m.id === current)
    ? [...VOICE_MODELS, { id: current, label: current, note: "not in this list" }]
    : VOICE_MODELS;
}

/** What Settings hands to channels without a model of their own. */
export function settingsVoiceModel(settings?: AppSettings | null) {
  return settings?.elevenLabsModel || BUILT_IN_VOICE_MODEL;
}

/** What a channel's videos are voiced with unless one picks its own — same order as the server. */
export function channelVoiceModel(channel?: Channel | null, settings?: AppSettings | null) {
  return channel?.voice?.modelId || settingsVoiceModel(settings);
}
