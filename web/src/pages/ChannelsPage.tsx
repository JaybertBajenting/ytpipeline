import { useEffect, useMemo, useState } from "react";
import {
  ArrowLeft, Camera, Clock, Image as ImageIcon, MonitorPlay, Music, Plus, RotateCcw, Save,
  ThumbsUp, Trash2, Tv, Volume2, X,
} from "lucide-react";
import {
  lengthBudget, newChannelTemplate, WPM_HIGH, WPM_LOW,
  type Channel, type ChannelPublishUpdate, type ChannelPublishView,
} from "@/lib/api";
import {
  useChannelPublish, useChannels, useCreateChannel, useDeleteChannel, useRuns,
  useSaveChannel, useSaveChannelPublish,
} from "@/lib/hooks";
import {
  Badge, Button, Callout, Card, CardHead, ConfirmModal, EmptyState, Field, Input, Modal,
  PageHead, SectionLabel, SecretInput, Select, Skeleton, Slider, Switch, Textarea, Toast,
  inputCls,
} from "@/components/ui";
import { cn } from "@/lib/utils";

/** channels/<id>.json — the id is a filename, so it has to be filename-safe. */
function slugify(name: string) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);
}

export function ChannelsPage() {
  const { data: channels, isLoading } = useChannels();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Channel | null>(null);

  const editing = channels?.find((c) => c.id === editingId) ?? null;

  if (editing) {
    return <ChannelEditor channel={editing} onBack={() => setEditingId(null)} />;
  }

  return (
    <>
      <PageHead
        title="Channels"
        desc="Each channel carries its own length target, voice, look, and publish destinations."
        actions={
          <Button variant="primary" onClick={() => setCreating(true)}>
            <Plus className="size-4" strokeWidth={2.5} /> New channel
          </Button>
        }
      />

      {isLoading ? (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-44" />)}
        </div>
      ) : !channels?.length ? (
        <EmptyState
          icon={<Tv className="size-6" />}
          title="No channels yet"
          desc="A channel holds the persona, length target, voice and look that every run for it inherits."
          action={
            <Button variant="primary" onClick={() => setCreating(true)}>
              <Plus className="size-4" /> New channel
            </Button>
          }
        />
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {channels.map((c, i) => (
            <ChannelCard
              key={c.id}
              c={c}
              index={i}
              onEdit={() => setEditingId(c.id)}
              onDelete={() => setDeleting(c)}
            />
          ))}
        </div>
      )}

      <NewChannelModal
        open={creating}
        onClose={() => setCreating(false)}
        existing={channels?.map((c) => c.id) ?? []}
        onCreated={(id) => { setCreating(false); setEditingId(id); }}
      />
      <DeleteChannelDialog target={deleting} onClose={() => setDeleting(null)} />
    </>
  );
}

// ─── Card ────────────────────────────────────────────────────────────────────

function ChannelCard({
  c, index, onEdit, onDelete,
}: { c: Channel; index: number; onEdit: () => void; onDelete: () => void }) {
  const { data: runs } = useRuns();
  const count = (runs ?? []).filter((m) => m.channel === c.id).length;
  const budget = lengthBudget(c.videoLengthMinMinutes, c.videoLengthMaxMinutes);

  return (
    <Card
      interactive
      onClick={onEdit}
      className="stagger group relative flex flex-col p-4"
      style={{ "--i": index } as React.CSSProperties}
    >
      <button
        title="Delete this channel"
        onClick={(e) => { e.stopPropagation(); onDelete(); }}
        className="absolute right-2 top-2 grid size-6 place-items-center rounded-md text-white/30 opacity-0 transition-all hover:bg-red-500/15 hover:text-red-400 focus-visible:opacity-100 group-hover:opacity-100"
      >
        <Trash2 className="size-3" />
      </button>

      <div className="flex items-start gap-2.5">
        <span className="grid size-9 shrink-0 place-items-center rounded-lg bg-amber-500/[0.12] text-sm font-bold text-amber-400">
          {(c.name || c.id).slice(0, 1).toUpperCase()}
        </span>
        <div className="min-w-0 flex-1 pr-6">
          <h3 className="truncate text-sm font-semibold text-white/90">{c.name}</h3>
          <p className="clamp-2 mt-0.5 text-[11px] leading-relaxed text-white/35">
            {c.tagline || <span className="italic text-white/20">no tagline</span>}
          </p>
        </div>
      </div>

      <div className="mt-3 grid grid-cols-2 gap-2">
        <Metric icon={<Clock className="size-3" />} label="Length" value={budget.minutesLabel} />
        <Metric
          icon={<ImageIcon className="size-3" />}
          label="Images"
          value={budget.isRange ? `${budget.beats.min}-${budget.beats.max}` : `~${budget.beats.min}`}
        />
      </div>

      <div className="mt-3 flex flex-wrap gap-1.5 border-t border-white/[0.05] pt-3">
        <Badge tone={c.voice.voiceId ? "info" : "muted"}>
          <Volume2 className="size-2.5" />
          {c.voice.voiceName || c.voice.voiceId || "global voice"}
        </Badge>
        <Badge tone={c.music ? "ok" : "muted"}>
          <Music className="size-2.5" /> {c.music ? "music" : "no music"}
        </Badge>
        {c.hasImageRule && <Badge tone="muted">image rule</Badge>}
        <span className="ml-auto text-[10px] tabular-nums text-white/25">
          {count} run{count === 1 ? "" : "s"}
        </span>
      </div>
    </Card>
  );
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) {
  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-2.5 py-2">
      <div className="flex items-center gap-1 text-[9px] font-semibold uppercase tracking-wider text-white/30">
        {icon} {label}
      </div>
      <div className="mt-0.5 text-xs font-semibold text-white/85">{value}</div>
    </div>
  );
}

// ─── Create ──────────────────────────────────────────────────────────────────

function NewChannelModal({
  open, onClose, existing, onCreated,
}: { open: boolean; onClose: () => void; existing: string[]; onCreated: (id: string) => void }) {
  const create = useCreateChannel();
  const [name, setName] = useState("");
  const [id, setId] = useState("");
  const [idEdited, setIdEdited] = useState(false);
  const [lo, setLo] = useState(3);
  const [hi, setHi] = useState(5);

  useEffect(() => {
    if (open) { setName(""); setId(""); setIdEdited(false); setLo(3); setHi(5); }
  }, [open]);

  // The id follows the name until the user takes it over — most people never touch it.
  const effectiveId = idEdited ? id : slugify(name);
  const taken = existing.includes(effectiveId);
  const valid = /^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])$/.test(effectiveId);
  const ready = !!name.trim() && valid && !taken;

  const budget = lengthBudget(lo, hi);

  async function submit() {
    if (!ready) return;
    const tpl = newChannelTemplate(name.trim());
    tpl.videoLengthMinMinutes = budget.lo;
    tpl.videoLengthMaxMinutes = budget.hi;
    tpl.videoLengthTargetMinutes = budget.lo;
    await create.mutateAsync({ id: effectiveId, channel: tpl });
    onCreated(effectiveId);
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="New channel"
      desc="Creates channels/<id>.json. You can fill in persona, voice and look next."
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!ready} loading={create.isPending} onClick={submit}>
            <Plus className="size-3.5" /> Create channel
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Input
          label="Channel name"
          placeholder="e.g. Japan Unpacked"
          value={name}
          autoFocus
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
        />

        <Field
          label="Channel id"
          hint="Lowercase letters, digits and dashes. Becomes the filename and can't be changed later."
        >
          <div className="relative">
            <input
              className={cn(inputCls, "pr-20 font-mono text-xs", (taken || (!!effectiveId && !valid)) && "border-red-500/40")}
              value={effectiveId}
              placeholder="japanunpacked"
              onChange={(e) => { setIdEdited(true); setId(slugify(e.target.value)); }}
            />
            <span className="absolute right-3 top-1/2 -translate-y-1/2 font-mono text-[10px] text-white/25">
              .json
            </span>
          </div>
        </Field>

        {taken && <Callout tone="err">A channel with the id <code>{effectiveId}</code> already exists.</Callout>}
        {!!name.trim() && !valid && !taken && (
          <Callout tone="err">
            That name doesn't produce a usable id. Set one manually — 2-40 characters, letters,
            digits or dashes.
          </Callout>
        )}

        <div className="space-y-3">
          <Slider
            label="Shortest video" min={1} max={20} step={1}
            value={lo}
            onChange={(v) => { setLo(v); if (v > hi) setHi(v); }}
            format={(v) => `${v} min`}
          />
          <Slider
            label="Longest video" min={1} max={20} step={1}
            value={hi}
            onChange={(v) => { setHi(v); if (v < lo) setLo(v); }}
            format={(v) => `${v} min`}
            hint={`≈ ${budget.wordsLabel} words and ${budget.isRange ? `${budget.beats.min}–${budget.beats.max}` : `~${budget.beats.min}`} images per video.`}
          />
        </div>

        <Callout tone="info">
          The new channel falls back to the shared <code>_default.image.md</code> rule until you
          add a <code>{effectiveId || "<id>"}.image.md</code> beside it.
        </Callout>

        {create.isError && <Callout tone="err">{(create.error as Error).message}</Callout>}
      </div>
    </Modal>
  );
}

// ─── Delete ──────────────────────────────────────────────────────────────────

function DeleteChannelDialog({ target, onClose }: { target: Channel | null; onClose: () => void }) {
  const del = useDeleteChannel();
  const { data: runs } = useRuns();

  if (!target) return null;
  const runCount = (runs ?? []).filter((m) => m.channel === target.id).length;

  return (
    <ConfirmModal
      open
      onClose={onClose}
      onConfirm={async () => { await del.mutateAsync(target.id); onClose(); }}
      loading={del.isPending}
      title="Delete this channel?"
      confirmLabel="Delete channel"
      confirmText={target.id}
    >
      <p className="text-sm text-white/70">{target.name}</p>
      <p className="text-xs text-white/40">
        Removes <code className="rounded bg-white/[0.07] px-1 font-mono text-[11px]">channels/{target.id}.json</code> and
        its saved publish tokens.
      </p>

      <Callout tone="info" title="What is kept">
        {runCount > 0
          ? <>Its {runCount} existing run{runCount === 1 ? "" : "s"} under <code>videos/{target.id}/</code> stay on disk — they'll just have no channel behind them. Delete videos individually if you want them gone.</>
          : <>Nothing else to clean up — this channel has no runs.</>}
        {target.hasImageRule && <> The hand-written <code>{target.id}.image.md</code> rule is also left alone.</>}
      </Callout>

      {del.isError && <Callout tone="err">{(del.error as Error).message}</Callout>}
    </ConfirmModal>
  );
}

// ─── Editor ──────────────────────────────────────────────────────────────────

/** Pristine publish draft — tokens start null so an untouched save keeps them. */
function emptyPublish(v: ChannelPublishView): ChannelPublishUpdate {
  return {
    youtubeChannelId: v.youtubeChannelId,
    facebookPageId: v.facebookPageId,
    instagramUserId: v.instagramUserId,
    publicVideoBaseUrl: v.publicVideoBaseUrl,
    facebookPageAccessToken: null,
    instagramAccessToken: null,
  };
}

function ChannelEditor({ channel, onBack }: { channel: Channel; onBack: () => void }) {
  const save = useSaveChannel();
  const pubQ = useChannelPublish(channel.id);
  const savePub = useSaveChannelPublish(channel.id);

  const [draft, setDraft] = useState<Channel>(channel);
  const [pub, setPub] = useState<ChannelPublishUpdate | null>(null);
  const [toast, setToast] = useState(false);

  useEffect(() => setDraft(channel), [channel]);
  useEffect(() => { if (pubQ.data) setPub(emptyPublish(pubQ.data)); }, [pubQ.data]);

  const channelDirty = useMemo(
    () => JSON.stringify(draft) !== JSON.stringify(channel),
    [draft, channel],
  );
  const pubDirty = useMemo(
    () => !!pub && !!pubQ.data && JSON.stringify(pub) !== JSON.stringify(emptyPublish(pubQ.data)),
    [pub, pubQ.data],
  );
  const dirty = channelDirty || pubDirty;

  const set = <K extends keyof Channel>(k: K, v: Channel[K]) => setDraft((d) => ({ ...d, [k]: v }));
  const setVoice = (patch: Partial<Channel["voice"]>) =>
    setDraft((d) => ({ ...d, voice: { ...d.voice, ...patch } }));
  const setYt = (patch: Partial<Channel["youtube"]>) =>
    setDraft((d) => ({ ...d, youtube: { ...d.youtube, ...patch } }));
  const setPubField = (patch: Partial<ChannelPublishUpdate>) =>
    setPub((p) => (p ? { ...p, ...patch } : p));

  const overriding = draft.voice.stability !== null || draft.voice.similarityBoost !== null;
  const budget = lengthBudget(draft.videoLengthMinMinutes, draft.videoLengthMaxMinutes);

  /** Keeps the pair ordered: dragging one handle past the other pushes the other along. */
  function setLength(which: "min" | "max", v: number) {
    setDraft((d) => {
      const lo = which === "min" ? v : Math.min(d.videoLengthMinMinutes, v);
      const hi = which === "max" ? v : Math.max(d.videoLengthMaxMinutes, v);
      // videoLengthTargetMinutes is legacy; the server re-syncs it to the lower bound on save.
      return { ...d, videoLengthMinMinutes: lo, videoLengthMaxMinutes: hi, videoLengthTargetMinutes: lo };
    });
  }

  function reset() {
    setDraft(channel);
    if (pubQ.data) setPub(emptyPublish(pubQ.data));
  }

  async function onSave() {
    // Two endpoints — the channel JSON and the gitignored token store — but one button, since
    // from the user's side it's all "this channel's settings".
    if (channelDirty) await save.mutateAsync({ id: draft.id, channel: draft });
    if (pubDirty && pub) await savePub.mutateAsync(pub);
    setToast(true);
    setTimeout(() => setToast(false), 2200);
  }

  const saving = save.isPending || savePub.isPending;
  const error = (save.error ?? savePub.error) as Error | null;

  return (
    <>
      <button
        onClick={onBack}
        className="mb-4 flex items-center gap-1.5 text-xs font-medium text-white/40 transition-colors hover:text-white/80"
      >
        <ArrowLeft className="size-3.5" /> All channels
      </button>

      <PageHead
        title={draft.name || draft.id}
        desc={<code className="rounded bg-white/[0.05] px-1.5 py-0.5 font-mono text-[11px]">channels/{draft.id}.json</code>}
        actions={
          <>
            {dirty && <Button variant="ghost" onClick={reset}><RotateCcw className="size-3.5" /> Reset</Button>}
            <Button variant="primary" disabled={!dirty} loading={saving} onClick={onSave}>
              <Save className="size-3.5" /> {dirty ? "Save changes" : "Saved"}
            </Button>
          </>
        }
      />

      {error && <div className="mb-4"><Callout tone="err">{error.message}</Callout></div>}

      <div className="grid gap-5 lg:grid-cols-2">
        {/* ── Identity ──────────────────────────────────────────────────── */}
        <Card>
          <CardHead title="Identity" desc="Feeds the script prompt as channel context." icon={<Tv className="size-3.5" />} />
          <div className="space-y-3.5 p-4">
            <Input label="Name" value={draft.name} onChange={(e) => set("name", e.target.value)} />
            <Input label="Tagline" value={draft.tagline} onChange={(e) => set("tagline", e.target.value)} />
            <Textarea label="Persona" rows={3} value={draft.persona} onChange={(e) => set("persona", e.target.value)} />
            <Input label="Audience" value={draft.audience} onChange={(e) => set("audience", e.target.value)} />
            <Input label="Tone" value={draft.tone} onChange={(e) => set("tone", e.target.value)} />
          </div>
        </Card>

        {/* ── Length ────────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title="Video length"
            desc="Sets the script's word budget — and with it how many images the run generates."
            icon={<Clock className="size-3.5" />}
          />
          <div className="space-y-4 p-4">
            <Slider
              label="Shortest" min={1} max={20} step={1}
              value={budget.lo}
              onChange={(v) => setLength("min", v)}
              format={(v) => `${v} min`}
            />
            <Slider
              label="Longest" min={1} max={20} step={1}
              value={budget.hi}
              onChange={(v) => setLength("max", v)}
              format={(v) => `${v} min`}
              hint={budget.isRange
                ? "The script may land anywhere in this range."
                : "Same as the shortest — asks for one specific length."}
            />

            <div className="grid grid-cols-3 gap-2">
              <Metric icon={<Clock className="size-3" />} label="Runtime" value={budget.minutesLabel} />
              <Metric icon={<Volume2 className="size-3" />} label="Words" value={budget.wordsLabel} />
              <Metric
                icon={<ImageIcon className="size-3" />}
                label="Images"
                value={budget.isRange ? `${budget.beats.min}-${budget.beats.max}` : `~${budget.beats.min}`}
              />
            </div>

            <Callout tone={budget.beats.max > 150 ? "warn" : "info"}>
              At {WPM_LOW}–{WPM_HIGH} words/min this asks for <strong>{budget.wordsLabel} words</strong>,
              split into 4–9-word beats — <strong>
              {budget.isRange ? `${budget.beats.min}–${budget.beats.max}` : `about ${budget.beats.min}`} images
              </strong>, one per beat.
              {budget.beats.max > 150 && " The top of that range is a long, quota-heavy image stage."}
            </Callout>
            <Switch
              checked={draft.music}
              onChange={(v) => set("music", v)}
              label="Background music"
              desc="Off ships voice-only videos, whatever the global music setting says."
            />
          </div>
        </Card>

        {/* ── Voice ─────────────────────────────────────────────────────── */}
        <Card>
          <CardHead
            title="Voice"
            desc="Per-channel ElevenLabs voice. Blank fields inherit the global .env value."
            icon={<Volume2 className="size-3.5" />}
          />
          <div className="space-y-3.5 p-4">
            <Input
              label="Voice ID"
              placeholder="inherit ELEVENLABS_VOICE_ID"
              className="font-mono text-xs"
              value={draft.voice.voiceId}
              onChange={(e) => setVoice({ voiceId: e.target.value })}
              hint="From ElevenLabs → Voices → the voice's ID."
            />
            <Input
              label="Voice label"
              placeholder="e.g. Og — gravelly"
              value={draft.voice.voiceName}
              onChange={(e) => setVoice({ voiceName: e.target.value })}
              hint="Shown around the UI, since the ID alone is unreadable."
            />
            <Select
              label="Model"
              value={draft.voice.modelId}
              onChange={(e) => setVoice({ modelId: e.target.value })}
              hint="Pick the model the voice was designed with. v3 is the most expressive; v2 is the safest for A/V sync — see the note below."
            >
              <option value="">Inherit global</option>
              <option value="eleven_v3">eleven_v3 — most expressive (5,000 char limit)</option>
              <option value="eleven_multilingual_v2">eleven_multilingual_v2 — 10,000 char limit</option>
              <option value="eleven_turbo_v2_5">eleven_turbo_v2_5 — faster</option>
              <option value="eleven_flash_v2_5">eleven_flash_v2_5 — fastest, 40,000 char limit</option>
            </Select>

            {draft.voice.modelId === "eleven_v3" && (
              <Callout tone="warn" title="v3 and image timing">
                The voice stage asks for per-character timestamps and uses them to sync each image
                to the exact word it's spoken over. ElevenLabs doesn't document whether
                <code> eleven_v3</code> returns alignment on the <code>with-timestamps</code>
                endpoint. If it doesn't, the run still completes — the video stage falls back to
                splitting each beat by word count, so cuts land close but not exactly on the
                narration. Check the voice stage log for
                "returned no alignment" after your first v3 run.
              </Callout>
            )}
            <Switch
              checked={overriding}
              onChange={(on) => setVoice(on
                ? { stability: 0.5, similarityBoost: 0.75 }
                : { stability: null, similarityBoost: null })}
              label="Override voice tuning"
              desc="Otherwise this channel uses the global stability / similarity."
            />
            {overriding && (
              <div className="space-y-4 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3">
                <Slider
                  label="Stability" min={0} max={1} step={0.05}
                  value={draft.voice.stability ?? 0.5}
                  onChange={(v) => setVoice({ stability: v })}
                  format={(v) => v.toFixed(2)}
                  hint="Lower = more expressive and variable; higher = flatter and more consistent."
                />
                <Slider
                  label="Similarity boost" min={0} max={1} step={0.05}
                  value={draft.voice.similarityBoost ?? 0.75}
                  onChange={(v) => setVoice({ similarityBoost: v })}
                  format={(v) => v.toFixed(2)}
                />
              </div>
            )}
            <Textarea
              label="Voice notes" rows={2}
              value={draft.voiceNotes}
              onChange={(e) => set("voiceNotes", e.target.value)}
              hint="Direction for the script's delivery. Context only — not sent to ElevenLabs as settings."
            />
          </div>
        </Card>

        {/* ── Look ──────────────────────────────────────────────────────── */}
        <Card>
          <CardHead title="Look" desc="Used when no <id>.image.md rule file exists." icon={<ImageIcon className="size-3.5" />} />
          <div className="space-y-3.5 p-4">
            <Textarea
              label="Visual style" rows={4}
              value={draft.visualStyle}
              onChange={(e) => set("visualStyle", e.target.value)}
            />
            {draft.hasImageRule ? (
              <Callout tone="info" title="Image rule file in use">
                <code>channels/{draft.id}.image.md</code> exists, so it takes precedence over the
                text above for image generation.
              </Callout>
            ) : (
              <Callout tone="warn">
                No <code>{draft.id}.image.md</code> — this channel falls back to the shared
                <code> _default.image.md</code> rule.
              </Callout>
            )}
          </div>
        </Card>

        {/* ── Publish destinations ──────────────────────────────────────── */}
        <Card className="lg:col-span-2">
          <CardHead
            title="Publish destinations"
            desc="Where THIS channel's videos go. The app credentials in Settings → Publishing are shared by every channel; these are not."
            icon={<MonitorPlay className="size-3.5" />}
          />
          <div className="p-4">
            {!pub ? (
              <Skeleton className="h-32" />
            ) : (
              <>
                <Callout tone="info" title="One app, many channels">
                  One Google OAuth client and one Meta app cover all your channels. But each
                  destination needs its own id and token here — a YouTube refresh token is bound
                  to one channel, and a Page token to one Page. Tokens are stored in the
                  gitignored <code>secrets.json</code>, never in the channel file.
                </Callout>

                <div className="mt-4 grid gap-5 md:grid-cols-3">
                  {/* YouTube */}
                  <div className="space-y-3">
                    <div className="flex items-center gap-1.5">
                      <MonitorPlay className="size-3.5 text-white/40" />
                      <SectionLabel>YouTube</SectionLabel>
                    </div>
                    <Input
                      label="Channel ID"
                      className="font-mono text-xs"
                      placeholder="UC…"
                      value={pub.youtubeChannelId}
                      onChange={(e) => setPubField({ youtubeChannelId: e.target.value })}
                      hint="Studio → Settings → Channel → Advanced."
                    />
                  </div>

                  {/* Facebook */}
                  <div className="space-y-3">
                    <div className="flex items-center gap-1.5">
                      <ThumbsUp className="size-3.5 text-white/40" />
                      <SectionLabel>Facebook Page</SectionLabel>
                    </div>
                    <Input
                      label="Page ID"
                      className="font-mono text-xs"
                      placeholder="from /me/accounts"
                      value={pub.facebookPageId}
                      onChange={(e) => setPubField({ facebookPageId: e.target.value })}
                    />
                    <SecretInput
                      label="Page access token"
                      saved={pubQ.data?.facebookPageAccessTokenSaved}
                      value={pub.facebookPageAccessToken}
                      onChange={(v) => setPubField({ facebookPageAccessToken: v })}
                      hint="Long-lived Page token, not a user token."
                    />
                  </div>

                  {/* Instagram */}
                  <div className="space-y-3">
                    <div className="flex items-center gap-1.5">
                      <Camera className="size-3.5 text-white/40" />
                      <SectionLabel>Instagram</SectionLabel>
                    </div>
                    <Input
                      label="IG user ID"
                      className="font-mono text-xs"
                      placeholder="business account id"
                      value={pub.instagramUserId}
                      onChange={(e) => setPubField({ instagramUserId: e.target.value })}
                    />
                    <SecretInput
                      label="Access token"
                      saved={pubQ.data?.instagramAccessTokenSaved}
                      value={pub.instagramAccessToken}
                      onChange={(v) => setPubField({ instagramAccessToken: v })}
                    />
                  </div>
                </div>

                <div className="mt-4">
                  <Input
                    label="Public video base URL"
                    placeholder="https://cdn.example.com/japanunpacked"
                    value={pub.publicVideoBaseUrl}
                    onChange={(e) => setPubField({ publicVideoBaseUrl: e.target.value })}
                    hint="Instagram makes Meta fetch the mp4 from a URL — it can't read local files, so this channel's renders must be hosted here first."
                  />
                </div>
              </>
            )}
          </div>
        </Card>

        {/* ── Script structure ──────────────────────────────────────────── */}
        <Card>
          <CardHead title="Script structure" desc="Beat-by-beat outline handed to the script stage." />
          <div className="space-y-2 p-4">
            {draft.scriptStructure.map((s, i) => (
              <div key={i} className="flex items-center gap-2">
                <span className="w-4 shrink-0 text-center text-[10px] font-bold tabular-nums text-white/25">
                  {i + 1}
                </span>
                <input
                  className={cn(inputCls, "py-1.5 text-xs")}
                  value={s}
                  onChange={(e) => {
                    const next = [...draft.scriptStructure];
                    next[i] = e.target.value;
                    set("scriptStructure", next);
                  }}
                />
                <button
                  onClick={() => set("scriptStructure", draft.scriptStructure.filter((_, j) => j !== i))}
                  className="shrink-0 rounded p-1 text-white/25 transition-colors hover:bg-red-500/10 hover:text-red-400"
                  title="Remove"
                >
                  <X className="size-3.5" />
                </button>
              </div>
            ))}
            <Button size="sm" variant="subtle" onClick={() => set("scriptStructure", [...draft.scriptStructure, ""])}>
              <Plus className="size-3" /> Add section
            </Button>
          </div>
        </Card>

        {/* ── YouTube defaults ──────────────────────────────────────────── */}
        <Card>
          <CardHead title="YouTube metadata defaults" desc="Applied to this channel's uploads." icon={<MonitorPlay className="size-3.5" />} />
          <div className="space-y-3.5 p-4">
            <Input
              label="Title pattern"
              value={draft.youtube.titlePattern}
              onChange={(e) => setYt({ titlePattern: e.target.value })}
              hint="{topic} is replaced with the run's topic."
            />
            <Input
              label="Category ID"
              className="font-mono text-xs"
              value={draft.youtube.categoryId}
              onChange={(e) => setYt({ categoryId: e.target.value })}
              hint="27 = Education, 28 = Science & Technology, 24 = Entertainment."
            />
            <Field label="Default tags" hint="Comma separated.">
              <input
                className={inputCls}
                value={draft.youtube.defaultTags.join(", ")}
                onChange={(e) => setYt({
                  defaultTags: e.target.value.split(",").map((t) => t.trim()).filter(Boolean),
                })}
              />
            </Field>
            <Textarea
              label="Description footer" rows={3}
              value={draft.youtube.descriptionFooter}
              onChange={(e) => setYt({ descriptionFooter: e.target.value })}
              hint="Appended to every description for this channel."
            />
          </div>
        </Card>
      </div>

      {dirty && (
        <div className="fade-in sticky bottom-4 mt-5 flex items-center gap-3 rounded-xl border border-amber-500/30 bg-[#16161a]/95 px-4 py-3 shadow-2xl shadow-black/50 backdrop-blur">
          <SectionLabel>Unsaved changes</SectionLabel>
          <div className="flex-1" />
          <Button variant="ghost" onClick={reset}>Discard</Button>
          <Button variant="primary" loading={saving} onClick={onSave}>
            <Save className="size-3.5" /> Save
          </Button>
        </div>
      )}

      <Toast show={toast}>Channel saved — next run uses these settings.</Toast>
    </>
  );
}
