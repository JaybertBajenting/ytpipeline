import { useState } from "react";
import { Clock, Image as ImageIcon, Sparkles } from "lucide-react";
import { lengthBudget } from "@/lib/api";
import { useChannels, useGenerateScript } from "@/lib/hooks";
import { useUi } from "@/lib/store";
import { Button, Callout, Input, Modal, Select, Switch } from "./ui";

/**
 * "New video" dialog. Reachable from the sidebar on every view, so starting a run never
 * means navigating somewhere first.
 */
export function Composer() {
  const open = useUi((s) => s.composerOpen);
  const setOpen = useUi((s) => s.setComposerOpen);
  const { data: channels } = useChannels();
  const [channel, setChannel] = useState("");
  const [topic, setTopic] = useState("");
  const [auto, setAuto] = useState(false);
  const gen = useGenerateScript();

  const channelId = channel || channels?.[0]?.id || "";
  const selected = channels?.find((c) => c.id === channelId);

  // Show what the channel's length range actually buys: word count and image count. Image spend
  // is the expensive part, so it belongs in front of the user before they press Generate rather
  // than in a log afterwards.
  const budget = lengthBudget(
    selected?.videoLengthMinMinutes ?? 5,
    selected?.videoLengthMaxMinutes ?? 5,
  );

  function submit() {
    if (!topic.trim() || !channelId) return;
    gen.mutate({ channel: channelId, topic: topic.trim(), auto });
    setTopic("");
    setOpen(false);
  }

  return (
    <Modal
      open={open}
      onClose={() => setOpen(false)}
      title="New video"
      desc="Generates a script first — every later stage is triggered from the video's page."
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
          <Button
            variant="primary"
            loading={gen.isPending}
            disabled={!topic.trim() || !channelId}
            onClick={submit}
          >
            <Sparkles className="size-3.5" />
            Generate script
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Select
          label="Channel"
          value={channelId}
          onChange={(e) => setChannel(e.target.value)}
          hint={selected?.tagline}
        >
          {channels?.map((c) => (
            <option key={c.id} value={c.id}>{c.name}</option>
          ))}
        </Select>

        <Input
          label="Topic"
          placeholder="e.g. How DNS actually works"
          value={topic}
          onChange={(e) => setTopic(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
          autoFocus
        />

        {/* Budget preview — derived from the channel's own length setting. */}
        <div className="grid grid-cols-2 gap-2">
          <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] px-3 py-2.5">
            <div className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider text-white/35">
              <Clock className="size-3" /> Target length
            </div>
            <div className="mt-1 text-sm font-semibold text-white">{budget.minutesLabel}</div>
            <div className="text-[11px] text-white/30">{budget.wordsLabel} words</div>
          </div>
          <div className="rounded-lg border border-white/[0.07] bg-white/[0.02] px-3 py-2.5">
            <div className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider text-white/35">
              <ImageIcon className="size-3" /> Images
            </div>
            <div className="mt-1 text-sm font-semibold text-white">
              {budget.isRange ? `${budget.beats.min}-${budget.beats.max}` : `~${budget.beats.min}`}
            </div>
            <div className="text-[11px] text-white/30">one per beat</div>
          </div>
        </div>

        <Switch
          checked={auto}
          onChange={setAuto}
          label="Auto-run everything"
          desc="Script → voice → images → video, approving both checkpoints automatically. Stops before upload."
        />

        {auto && budget.beats.max > 150 && (
          <Callout tone="warn" title="Long run ahead">
            Up to {budget.beats.max} images at this length. Image generation is the slow,
            quota-consuming stage — consider lowering this channel's length range first.
          </Callout>
        )}

        {gen.isError && (
          <Callout tone="err">{(gen.error as Error).message}</Callout>
        )}
      </div>
    </Modal>
  );
}
