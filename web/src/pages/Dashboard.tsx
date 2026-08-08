import { ArrowRight, CheckCircle2, Film, Image as ImageIcon, Loader2, Plus, Tv, Upload } from "lucide-react";
import { lengthBudget, stagesDone } from "@/lib/api";
import { useChannels, useRuns, useSettings } from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { VideoCard } from "@/components/VideoCard";
import {
  Badge, Button, Card, CardHead, EmptyState, KeyValue, PageHead, StatTile,
} from "@/components/ui";

export function Dashboard() {
  const go = useUi((s) => s.go);
  const jobs = useUi((s) => s.jobs);
  const setComposerOpen = useUi((s) => s.setComposerOpen);
  const { data: runs } = useRuns();
  const { data: channels } = useChannels();
  const { data: settings } = useSettings();

  const all = runs ?? [];
  const ready = all.filter((m) => m.stages.video.done && !m.stages.upload.done);
  const uploaded = all.filter((m) => m.stages.upload.done);
  const running = Object.keys(jobs).length;
  const totalImages = all.reduce((n, m) => n + (m.stages.images.count ?? 0), 0);

  const recent = [...all]
    .sort((a, b) => +new Date(b.createdUtc) - +new Date(a.createdUtc))
    .slice(0, 4);

  return (
    <>
      <PageHead
        title="Dashboard"
        desc="Pipeline at a glance."
        actions={
          <Button variant="primary" onClick={() => setComposerOpen(true)}>
            <Plus className="size-4" strokeWidth={2.5} /> New video
          </Button>
        }
      />

      {/* ── Stats ─────────────────────────────────────────────────────────── */}
      <div className="mb-6 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile
          label="Videos" value={all.length}
          sub={`${all.filter((m) => stagesDone(m) === 5).length} fully complete`}
          icon={<Film className="size-3.5" />}
        />
        <StatTile
          label="Ready to upload" value={ready.length}
          sub={ready.length ? "approved, awaiting publish" : "nothing waiting"}
          icon={<CheckCircle2 className="size-3.5" />}
          tone={ready.length ? "emerald" : "base"}
        />
        <StatTile
          label="Running" value={running}
          sub={running ? "jobs in flight" : "idle"}
          icon={running ? <Loader2 className="size-3.5 animate-spin" /> : <Loader2 className="size-3.5" />}
          tone={running ? "amber" : "base"}
        />
        <StatTile
          label="Frames generated" value={totalImages.toLocaleString()}
          sub="across all runs"
          icon={<ImageIcon className="size-3.5" />}
        />
      </div>

      <div className="grid gap-6 lg:grid-cols-[1fr_300px]">
        {/* ── Recent videos ───────────────────────────────────────────────── */}
        <section>
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-white/80">Recent</h2>
            {all.length > 4 && (
              <Button size="sm" variant="ghost" onClick={() => go("videos")}>
                View all <ArrowRight className="size-3" />
              </Button>
            )}
          </div>

          {recent.length === 0 ? (
            <EmptyState
              icon={<Film className="size-6" />}
              title="No videos yet"
              desc="Pick a channel, give it a topic, and the pipeline takes it from script to final cut."
              action={
                <Button variant="primary" onClick={() => setComposerOpen(true)}>
                  <Plus className="size-4" /> New video
                </Button>
              }
            />
          ) : (
            <div className="grid grid-cols-[repeat(auto-fill,minmax(230px,1fr))] gap-4">
              {recent.map((m, i) => <VideoCard key={m.runId} m={m} index={i} />)}
            </div>
          )}
        </section>

        {/* ── Side rail ───────────────────────────────────────────────────── */}
        <div className="space-y-4">
          {/* Channels */}
          <Card>
            <CardHead
              title="Channels"
              icon={<Tv className="size-3.5" />}
              right={
                <Button size="xs" variant="ghost" onClick={() => go("channels")}>
                  Edit
                </Button>
              }
            />
            <div className="p-3">
              {channels?.length ? (
                <div className="space-y-2">
                  {channels.map((c) => (
                    <button
                      key={c.id}
                      onClick={() => go("channels")}
                      className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-white/[0.05]"
                    >
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-xs font-medium text-white/80">{c.name}</span>
                        <span className="block truncate text-[10px] text-white/30">
                          {lengthBudget(c.videoLengthMinMinutes, c.videoLengthMaxMinutes).minutesLabel}
                          {c.voice.voiceName ? ` · ${c.voice.voiceName}` : ""}
                          {c.music ? "" : " · no music"}
                        </span>
                      </span>
                      <span className="shrink-0 text-[10px] tabular-nums text-white/25">
                        {all.filter((m) => m.channel === c.id).length}
                      </span>
                    </button>
                  ))}
                </div>
              ) : (
                <p className="px-2 py-1 text-xs text-white/30">No channels found.</p>
              )}
            </div>
          </Card>

          {/* Pipeline config summary */}
          <Card>
            <CardHead
              title="Configuration"
              icon={<ImageIcon className="size-3.5" />}
              right={
                <Button size="xs" variant="ghost" onClick={() => go("settings")}>
                  Open
                </Button>
              }
            />
            <div className="px-4 py-3">
              <KeyValue
                k="Image provider"
                v={settings ? <Badge tone="info">{settings.imageProvider}</Badge> : "…"}
              />
              {settings?.imageProvider === "gflow" && (
                <>
                  <KeyValue k="Model" v={settings.gflow.model} />
                  <KeyValue k="Concurrency" v={settings.gflow.maxConcurrent} />
                </>
              )}
              <KeyValue k="Voice gain" v={settings ? `${settings.voiceGain.toFixed(2)}×` : "…"} />
              <KeyValue
                k="Music"
                v={settings?.musicEnabled
                  ? `on · ${Math.round(settings.musicVolume * 100)}%`
                  : "off"}
              />
            </div>
          </Card>

          {/* Publish status */}
          <Card>
            <CardHead title="Publishing" icon={<Upload className="size-3.5" />} />
            <div className="px-4 py-3">
              <KeyValue k="Uploaded" v={uploaded.length} />
              <KeyValue k="Awaiting" v={ready.length} />
              <div className="mt-3">
                <Button size="sm" variant="secondary" className="w-full" onClick={() => go("settings")}>
                  Connect platforms
                </Button>
              </div>
            </div>
          </Card>
        </div>
      </div>
    </>
  );
}
