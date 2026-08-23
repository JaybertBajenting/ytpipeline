import { useEffect, useMemo, useState } from "react";
// lucide dropped brand marks, so each platform gets the closest generic glyph.
import {
  Camera, Image as ImageIcon, MonitorPlay, Music, Plug, RotateCcw, Save, SlidersHorizontal,
  TerminalSquare, ThumbsUp, Tv, Volume2,
} from "lucide-react";
import type { AppSettings, SecretsUpdate, SecretsView } from "@/lib/api";
import {
  useChannels, useCheckGflow, useProviderHealth, useSaveSecrets, useSaveSettings,
  useSecrets, useSettings,
} from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { PublishingGuide } from "@/components/PublishingGuide";
import {
  Badge, Button, Callout, Card, CardHead, Field, Input, PageHead, SectionLabel, SecretInput,
  Select, Skeleton, Slider, Switch, TabList, TabPanel, TabTrigger, Tabs, Toast, inputCls,
} from "@/components/ui";

export function SettingsPage() {
  const [tab, setTab] = useState("general");

  return (
    <>
      <PageHead title="Settings" desc="Applies to every channel. Per-channel overrides live on the Channels page." />

      <Tabs value={tab} onChange={setTab}>
        <TabList>
          <TabTrigger id="general" icon={<SlidersHorizontal className="size-3.5" />}>Audio</TabTrigger>
          <TabTrigger id="images" icon={<ImageIcon className="size-3.5" />}>Images</TabTrigger>
          <TabTrigger id="publishing" icon={<Plug className="size-3.5" />}>Publishing</TabTrigger>
        </TabList>

        <TabPanel id="general"><PipelineSettings section="audio" /></TabPanel>
        <TabPanel id="images"><PipelineSettings section="images" /></TabPanel>
        <TabPanel id="publishing"><PublishingSettings /></TabPanel>
      </Tabs>
    </>
  );
}

// ─── settings.json (audio + images) ──────────────────────────────────────────

function PipelineSettings({ section }: { section: "audio" | "images" }) {
  const { data: server, isLoading } = useSettings();
  const save = useSaveSettings();
  const check = useCheckGflow();
  const [draft, setDraft] = useState<AppSettings | null>(null);
  const [toast, setToast] = useState(false);

  useEffect(() => { if (server) setDraft(server); }, [server]);

  const dirty = useMemo(
    () => !!draft && !!server && JSON.stringify(draft) !== JSON.stringify(server),
    [draft, server],
  );

  if (isLoading || !draft) return <Skeleton className="h-64" />;

  const set = <K extends keyof AppSettings>(k: K, v: AppSettings[K]) =>
    setDraft((d) => (d ? { ...d, [k]: v } : d));
  const setGflow = (patch: Partial<AppSettings["gflow"]>) =>
    setDraft((d) => (d ? { ...d, gflow: { ...d.gflow, ...patch } } : d));

  async function onSave() {
    if (!draft) return;
    await save.mutateAsync(draft);
    setToast(true);
    setTimeout(() => setToast(false), 2000);
  }

  const SaveBar = (
    <div className="mt-5 flex items-center justify-end gap-2">
      {dirty && (
        <Button variant="ghost" onClick={() => server && setDraft(server)}>
          <RotateCcw className="size-3.5" /> Reset
        </Button>
      )}
      <Button variant="primary" disabled={!dirty} loading={save.isPending} onClick={onSave}>
        <Save className="size-3.5" /> {dirty ? "Save changes" : "Saved"}
      </Button>
    </div>
  );

  if (section === "audio") {
    return (
      <>
        <div className="grid gap-5 lg:grid-cols-2">
          <Card>
            <CardHead title="Voice" desc="Applied when the final video is muxed." icon={<Volume2 className="size-3.5" />} />
            <div className="p-4">
              <Slider
                label="Voice gain" min={0.1} max={4} step={0.05}
                value={draft.voiceGain}
                onChange={(v) => set("voiceGain", v)}
                format={(v) => `${v.toFixed(2)}×`}
                hint="1.00× is the original ElevenLabs level. Above ~2× risks clipping."
              />
            </div>
          </Card>

          <Card>
            <CardHead title="Background music" desc="A channel with music off always ships silent." icon={<Music className="size-3.5" />} />
            <div className="space-y-4 p-4">
              <Switch
                checked={draft.musicEnabled}
                onChange={(v) => set("musicEnabled", v)}
                label="Enable background music"
                desc="Master switch — per-channel settings can still opt out."
              />
              {draft.musicEnabled && (
                <Slider
                  label="Music level" min={0} max={1} step={0.01}
                  value={draft.musicVolume}
                  onChange={(v) => set("musicVolume", v)}
                  format={(v) => `${Math.round(v * 100)}%`}
                  hint="Relative to the voice. 15% sits under narration without competing."
                />
              )}
            </div>
          </Card>
        </div>
        {SaveBar}
        <Toast show={toast}>Settings saved.</Toast>
      </>
    );
  }

  // ── Images ────────────────────────────────────────────────────────────────
  const isGflow = draft.imageProvider === "gflow";

  return (
    <>
      <div className="space-y-5">
        <Card>
          <CardHead title="Provider" desc="Which backend generates the frames." icon={<ImageIcon className="size-3.5" />} />
          <div className="space-y-4 p-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <ProviderOption
                active={draft.imageProvider === "codex"}
                onClick={() => set("imageProvider", "codex")}
                title="Codex CLI"
                desc="One agent run generates every beat. Fully unattended; spends your ChatGPT plan."
              />
              <ProviderOption
                active={isGflow}
                onClick={() => set("imageProvider", "gflow")}
                title="gflow / Google Flow"
                desc="One image per call through a headed Chrome window. Spends Google AI Pro credits."
              />
            </div>
            {isGflow && (
              <Callout tone="warn" title="Needs a signed-in browser">
                gflow drives a real Chrome window, so leave it open and signed in while a run works.
              </Callout>
            )}

            {/* Frames are bulk work; the thumbnail is one image that has to follow a long style
                rule. Splitting them lets the fussy job go to the backend that reads the rule
                properly without slowing the bulk job down. */}
            <div className="border-t border-white/[0.06] pt-4">
              <Switch
                checked={!!draft.thumbnailProvider}
                onChange={(on) => set("thumbnailProvider", on ? (isGflow ? "codex" : "gflow") : "")}
                label={`Use ${isGflow ? "Codex" : "gflow"} for the thumbnail`}
                desc={`Frames keep using ${isGflow ? "gflow" : "Codex"}. Only the single publish thumbnail switches.`}
              />
              {draft.thumbnailProvider && (
                <div className="mt-3">
                <Callout tone="info" title="Why you might want this">
                  A thumbnail is one image that has to obey the whole
                  <code> _default.thumbnail.md</code> rule — white to the edges, two or three
                  elements, red doing one job. An agent that reads the rule end to end tends to hold
                  that better than a fast renderer, and at one image per video the extra time costs
                  nothing. Frames are unaffected.
                </Callout>
                </div>
              )}
            </div>

            {/* Fallback: the whole point is finishing a video when a daily quota runs out
                part-way, without hand-holding the switchover. */}
            <div className="border-t border-white/[0.06] pt-4">
              <Switch
                checked={!!draft.imageFallbackProvider}
                onChange={(on) => set(
                  "imageFallbackProvider",
                  on ? (isGflow ? "codex" : "gflow") : "",
                )}
                label={`Finish with ${isGflow ? "Codex" : "gflow"} if ${isGflow ? "gflow" : "Codex"} runs out`}
                desc="When the primary stops short — usually a daily quota — the other provider generates the beats that are still missing, in the same run."
              />

              {draft.imageFallbackProvider && (
                <div className="mt-3 space-y-2">
                  <Callout tone="info" title="How the switching works">
                    The provider above is the <strong>preferred</strong> one, not a fixed one. When
                    it reports its daily quota gone it's benched, and later runs go straight to the
                    standby — no attempts wasted rediscovering the same wall. After a cooldown the
                    preferred provider is tried again automatically, so the real reset is discovered
                    rather than guessed, and it takes back over the moment it works. Only quota
                    errors bench a provider; a sign-in or CAPTCHA failure is surfaced instead, since
                    that's fixable.
                  </Callout>
                  <Callout tone="warn" title="Expect a visible seam">
                    The two renderers don't look identical even on the same style rule, so a video
                    finished across both can show a shift where the handover happened. Fine for
                    getting a video out; not ideal if frame-to-frame consistency matters more.
                  </Callout>
                  <ProviderHealthPanel />
                </div>
              )}
            </div>
          </div>
        </Card>

        {isGflow && (
          <Card>
            <CardHead
              title="gflow CLI"
              desc="How the pipeline shells out to the flow executable."
              icon={<TerminalSquare className="size-3.5" />}
              right={
                <Button size="sm" loading={check.isPending} onClick={() => check.mutate()}>
                  Test connection
                </Button>
              }
            />
            <div className="space-y-3.5 p-4">
              {check.data && (
                <Callout tone={check.data.installed ? "info" : "err"}
                         title={check.data.installed ? "Reachable" : "Not reachable"}>
                  <pre className="m-0 max-h-24 overflow-auto whitespace-pre-wrap font-mono text-[10px]">
                    {check.data.installed ? check.data.version : check.data.error}
                  </pre>
                </Callout>
              )}

              <Input
                label="Executable"
                className="font-mono text-xs"
                value={draft.gflow.executable}
                onChange={(e) => setGflow({ executable: e.target.value })}
                hint="Full path is safest — a bare name can mis-resolve under .NET."
              />

              <div className="grid gap-3.5 sm:grid-cols-2">
                <Input
                  label="Model"
                  className="font-mono text-xs"
                  value={draft.gflow.model}
                  onChange={(e) => setGflow({ model: e.target.value })}
                  hint="flow-agent: gem_pix_2, narwhal, harbor_seal. gflow: nano2, nano-pro, imagen4."
                />
                <Select
                  label="Aspect"
                  value={draft.gflow.aspect}
                  onChange={(e) => setGflow({ aspect: e.target.value })}
                  hint="Must mean 16:9 — the CLI's own default is vertical."
                >
                  <option value="landscape">landscape</option>
                  <option value="16:9">16:9</option>
                  <option value="4x3">4x3</option>
                  <option value="square">square</option>
                  <option value="3x4">3x4</option>
                  <option value="portrait">portrait</option>
                </Select>
              </div>

              <div className="grid gap-3.5 sm:grid-cols-2">
                <Field label="Concurrent images" hint="1 for gflow-cli (single browser). ~4-6 for flow-agent.">
                  <input
                    type="number" min={1} max={12}
                    className={inputCls}
                    value={draft.gflow.maxConcurrent}
                    onChange={(e) => setGflow({ maxConcurrent: Number(e.target.value) })}
                  />
                </Field>
                <Field label="Timeout (seconds)" hint="Per image, before the attempt is abandoned.">
                  <input
                    type="number" min={30} max={1800}
                    className={inputCls}
                    value={draft.gflow.timeoutSeconds}
                    onChange={(e) => setGflow({ timeoutSeconds: Number(e.target.value) })}
                  />
                </Field>
              </div>

              <ArgsField
                label="Args template"
                hint='Placed before the prompt. gflow-cli uses "image t2i"; flow-agent uses "image".'
                value={draft.gflow.argsTemplate}
                onChange={(v) => setGflow({ argsTemplate: v })}
              />
              <div className="grid gap-3.5 sm:grid-cols-2">
                <Input
                  label="Output flag"
                  className="font-mono text-xs"
                  value={draft.gflow.outputFlag}
                  onChange={(e) => setGflow({ outputFlag: e.target.value })}
                />
                <Input
                  label="Project"
                  value={draft.gflow.project}
                  onChange={(e) => setGflow({ project: e.target.value })}
                  hint="Blank = the CLI's default."
                />
              </div>
              <ArgsField
                label="Check args" hint="Used by Test connection."
                value={draft.gflow.checkArgs}
                onChange={(v) => setGflow({ checkArgs: v })}
              />
              <ArgsField
                label="Extra args" hint="Appended verbatim to every call."
                value={draft.gflow.extraArgs}
                onChange={(v) => setGflow({ extraArgs: v })}
              />
            </div>
          </Card>
        )}
      </div>
      {SaveBar}
      <Toast show={toast}>Settings saved.</Toast>
    </>
  );
}

/** Live view of which providers are benched — makes the automatic switching legible. */
function ProviderHealthPanel() {
  const { data } = useProviderHealth();
  if (!data?.length) return null;

  return (
    <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] p-3">
      <div className="mb-2"><SectionLabel>Provider status</SectionLabel></div>
      <div className="space-y-1.5">
        {data.map((p) => (
          <div key={p.provider} className="flex items-center gap-2 text-[11px]">
            <span className="w-14 shrink-0 font-mono text-white/70">{p.provider}</span>
            {p.preferred && <Badge tone="info">preferred</Badge>}
            {p.standby && <Badge tone="muted">standby</Badge>}
            {p.benched
              ? <Badge tone="warn">
                  quota — retry in {p.retryInMinutes !== null ? `${Math.max(0, Math.round(p.retryInMinutes))} min` : "a while"}
                </Badge>
              : <Badge tone="ok">available</Badge>}
            {p.consecutiveProbeFailures > 0 && (
              <span className="text-white/25">
                {p.consecutiveProbeFailures} retry{p.consecutiveProbeFailures === 1 ? "" : "ies"} still out
              </span>
            )}
          </div>
        ))}
      </div>
      {data.some((p) => p.benched && p.lastError) && (
        <p className="mt-2 border-t border-white/[0.06] pt-2 font-mono text-[10px] leading-relaxed text-white/30">
          {data.find((p) => p.benched && p.lastError)!.lastError}
        </p>
      )}
    </div>
  );
}

function ProviderOption({
  active, onClick, title, desc,
}: { active: boolean; onClick: () => void; title: string; desc: string }) {
  return (
    <button
      onClick={onClick}
      className={`rounded-lg border p-3.5 text-left transition-colors ${
        active
          ? "border-amber-500/40 bg-amber-500/[0.08]"
          : "border-white/[0.08] bg-white/[0.02] hover:border-white/[0.16]"
      }`}
    >
      <div className="flex items-center gap-2">
        <span className={`size-2 rounded-full ${active ? "bg-amber-500" : "bg-white/20"}`} />
        <span className={`text-xs font-semibold ${active ? "text-amber-400" : "text-white/70"}`}>
          {title}
        </span>
      </div>
      <p className="mt-1.5 text-[11px] leading-relaxed text-white/35">{desc}</p>
    </button>
  );
}

/** Space-separated editor for the CLI's string[] arg fields. */
function ArgsField({
  label, hint, value, onChange,
}: { label: string; hint?: string; value: string[]; onChange: (v: string[]) => void }) {
  return (
    <Field label={label} hint={hint}>
      <input
        className={`${inputCls} font-mono text-xs`}
        value={value.join(" ")}
        onChange={(e) => onChange(e.target.value.split(/\s+/).filter(Boolean))}
      />
    </Field>
  );
}

// ─── secrets.json — APP-LEVEL credentials only ───────────────────────────────

/**
 * Pristine draft from the server's view. Secrets start as `null` — "untouched" — so pressing
 * Save without touching them leaves the stored values alone. `""` is reserved for an explicit
 * Clear, which is the only way to remove a credential.
 */
function emptyUpdate(v: SecretsView): SecretsUpdate {
  return {
    youtube: {
      enabled: v.youtube.enabled,
      clientId: v.youtube.clientId,
      clientSecret: null,
      defaultPrivacy: v.youtube.defaultPrivacy,
    },
    facebook: { enabled: v.facebook.enabled, appId: v.facebook.appId, appSecret: null },
    instagram: { enabled: v.instagram.enabled, appId: v.instagram.appId, appSecret: null },
  };
}

function PublishingSettings() {
  const { data: view, isLoading } = useSecrets();
  const { data: channels } = useChannels();
  const go = useUi((s) => s.go);
  const save = useSaveSecrets();
  const [draft, setDraft] = useState<SecretsUpdate | null>(null);
  const [toast, setToast] = useState(false);

  useEffect(() => { if (view) setDraft(emptyUpdate(view)); }, [view]);

  if (isLoading || !draft || !view) return <Skeleton className="h-64" />;

  const dirty = JSON.stringify(draft) !== JSON.stringify(emptyUpdate(view));

  const setYt = (p: Partial<SecretsUpdate["youtube"]>) =>
    setDraft((d) => (d ? { ...d, youtube: { ...d.youtube, ...p } } : d));
  const setFb = (p: Partial<SecretsUpdate["facebook"]>) =>
    setDraft((d) => (d ? { ...d, facebook: { ...d.facebook, ...p } } : d));
  const setIg = (p: Partial<SecretsUpdate["instagram"]>) =>
    setDraft((d) => (d ? { ...d, instagram: { ...d.instagram, ...p } } : d));

  async function onSave() {
    if (!draft) return;
    await save.mutateAsync(draft);
    setToast(true);
    setTimeout(() => setToast(false), 2200);
  }

  return (
    <>
      {/* The shared-vs-per-channel split is the single most confusing thing about publishing
          setup, so it is stated before any field. */}
      <Callout tone="info" title="These credentials are SHARED by every channel">
        This page holds your <strong>developer app</strong> identity — one Google OAuth client and
        one Meta app cover all your channels. Where each video actually goes lives on the channel:
        YouTube channel id, Facebook Page id + Page token, Instagram user id + token are set per
        channel, because each token is bound to one destination.
      </Callout>

      <div className="mt-3">
        <Callout tone="warn" title="Shared quota, not shared per channel">
          YouTube's upload quota is per <strong>Google Cloud project</strong>. Every channel using
          this OAuth client draws on the same ~10,000 units/day — about <strong>6 uploads a day in
          total</strong>, not 6 per channel. Channels on a different Google account need their own
          Cloud project and client.
        </Callout>
      </div>

      <div className="mt-3">
        <Callout tone="info">
          Values are written to <code>secrets.json</code> in the workspace root, which is
          gitignored, and are never sent back to this page. A blank field keeps what's stored;
          use <strong>clear</strong> to remove one.
        </Callout>
      </div>

      {/* Jump-off to the per-channel half of the setup. */}
      <div className="mt-4 flex flex-wrap items-center gap-2 rounded-lg border border-white/[0.07] bg-white/[0.02] px-3.5 py-3">
        <Tv className="size-3.5 shrink-0 text-white/40" />
        <span className="text-xs text-white/55">
          Per-channel destinations:
        </span>
        <span className="text-xs text-white/30">
          {channels?.length
            ? channels.map((c) => c.name).join(" · ")
            : "no channels yet"}
        </span>
        <div className="flex-1" />
        <Button size="sm" onClick={() => go("channels")}>Open Channels</Button>
      </div>

      <div className="mt-5 space-y-5">
        {/* ── YouTube ───────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title="YouTube — OAuth client"
            desc="Data API v3. The only publisher currently implemented."
            icon={<MonitorPlay className="size-3.5" />}
            right={<Badge tone={view.youtube.enabled ? "ok" : "muted"}>
              {view.youtube.enabled ? "enabled" : "off"}
            </Badge>}
          />
          <div className="space-y-3.5 p-4">
            <Switch
              checked={draft.youtube.enabled}
              onChange={(v) => setYt({ enabled: v })}
              label="Enable YouTube publishing"
            />
            <div className="grid gap-3.5 sm:grid-cols-2">
              <Input
                label="OAuth client ID"
                className="font-mono text-xs"
                placeholder="…apps.googleusercontent.com"
                value={draft.youtube.clientId}
                onChange={(e) => setYt({ clientId: e.target.value })}
              />
              <SecretInput
                label="OAuth client secret"
                saved={view.youtube.clientSecretSaved}
                value={draft.youtube.clientSecret}
                onChange={(v) => setYt({ clientSecret: v })}
              />
            </div>
            <Select
              label="Default privacy"
              value={draft.youtube.defaultPrivacy}
              onChange={(e) => setYt({ defaultPrivacy: e.target.value })}
              hint="What new uploads start as."
            >
              <option value="private">private</option>
              <option value="unlisted">unlisted</option>
              <option value="public">public</option>
            </Select>
            <PublishingGuide platform="youtube" />
          </div>
        </Card>

        {/* ── Facebook ──────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title="Facebook — Meta app"
            desc="Graph API video publishing. Page id and Page token are per channel."
            icon={<ThumbsUp className="size-3.5" />}
            right={<Badge tone={view.facebook.enabled ? "ok" : "muted"}>
              {view.facebook.enabled ? "enabled" : "off"}
            </Badge>}
          />
          <div className="space-y-3.5 p-4">
            <Switch
              checked={draft.facebook.enabled}
              onChange={(v) => setFb({ enabled: v })}
              label="Enable Facebook publishing"
            />
            <div className="grid gap-3.5 sm:grid-cols-2">
              <Input
                label="App ID"
                className="font-mono text-xs"
                value={draft.facebook.appId}
                onChange={(e) => setFb({ appId: e.target.value })}
              />
              <SecretInput
                label="App secret"
                saved={view.facebook.appSecretSaved}
                value={draft.facebook.appSecret}
                onChange={(v) => setFb({ appSecret: v })}
              />
            </div>
            <PublishingGuide platform="facebook" />
          </div>
        </Card>

        {/* ── Instagram ─────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title="Instagram — Meta app"
            desc="Usually the same app as Facebook. IG user id and token are per channel."
            icon={<Camera className="size-3.5" />}
            right={<Badge tone={view.instagram.enabled ? "ok" : "muted"}>
              {view.instagram.enabled ? "enabled" : "off"}
            </Badge>}
          />
          <div className="space-y-3.5 p-4">
            <Switch
              checked={draft.instagram.enabled}
              onChange={(v) => setIg({ enabled: v })}
              label="Enable Instagram publishing"
            />
            <div className="grid gap-3.5 sm:grid-cols-2">
              <Input
                label="App ID"
                className="font-mono text-xs"
                placeholder="usually the same Meta app"
                value={draft.instagram.appId}
                onChange={(e) => setIg({ appId: e.target.value })}
              />
              <SecretInput
                label="App secret"
                saved={view.instagram.appSecretSaved}
                value={draft.instagram.appSecret}
                onChange={(v) => setIg({ appSecret: v })}
              />
            </div>
            <PublishingGuide platform="instagram" />
          </div>
        </Card>
      </div>

      <div className="mt-5 flex items-center justify-end gap-2">
        {dirty && (
          <Button variant="ghost" onClick={() => view && setDraft(emptyUpdate(view))}>
            <RotateCcw className="size-3.5" /> Reset
          </Button>
        )}
        <Button variant="primary" disabled={!dirty} loading={save.isPending} onClick={onSave}>
          <Save className="size-3.5" /> {dirty ? "Save credentials" : "Saved"}
        </Button>
      </div>

      {save.isError && (
        <div className="mt-3"><Callout tone="err">{(save.error as Error).message}</Callout></div>
      )}

      <Toast show={toast}>Credentials saved to secrets.json.</Toast>
    </>
  );
}
