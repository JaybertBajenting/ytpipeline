# The Old Humans — voice

Not read by the pipeline. This is the brief for building the channel's narrator in
**ElevenLabs → Voice Design (Text to Voice)**, and the settings to put back into
`the-old-humans.json` afterwards.

Measured from six Ink Explainer videos: **~210 words per minute**, i.e. a fast conversational
read with almost no dead air. The first voice built here came out at 167 wpm and, more
importantly, sounded synthetic.

---

## Why attempt 1 sounded like AI

The first brief asked for *"clean and dry, no reverb, no background noise, clear consonants,
recorded in a treated room."* Every one of those words pushes the generator toward a polished
audiobook read — and polish is exactly what the ear identifies as synthetic. Real speech has
breath, uneven pace, a little rasp, and a room around it.

It also described a **role** ("narrator", "conversational", "warm") rather than a **person**.
Roles produce averages. The fix is to describe someone specific, doing something specific, with
flaws named out loud.

## 1. Voice description prompt (paste into Voice Design)

    A 27-year-old American man talking across a kitchen table about something he read last night
    and can't shut up about. He is not a narrator and has never been in a recording booth.
    Mid-range, slightly husky, a touch nasal, with a dry rasp creeping in at the ends of sentences
    from talking too much. Audible breath before the long sentences. He speeds up when he finds
    something interesting and flattens out when he's stating the obvious. Small mouth and lip
    sounds between phrases, the occasional swallow. Completely deadpan on the funny lines — he
    never sells the joke. Recorded on a decent USB microphone in a small carpeted bedroom: you can
    faintly hear the room. No announcer polish, no radio warmth, no smile in the voice, no
    theatrical emphasis, no breathy intimacy.

## 2. Preview text (paste into the preview box)

Written deliberately uneven — a false start, a fragment, an aside, a mid-thought correction.
Voice Design shapes the voice around this text, so clean formal prose here gives you a clean
formal voice. That is half of why attempt 1 sounded the way it did.

    Okay so — you're not thirsty right now. You haven't thought about water once today, because it
    comes out of a pipe in your kitchen, clean, whenever you want it. For almost all of human
    history that was the single hardest problem a person had. And I mean hardest. People died of
    it constantly. At Blombos Cave in South Africa they were grinding pigment into paint a hundred
    thousand years ago — careful, patient, clever people — and they still had to guess whether the
    water in front of them was going to kill them. But here's the thing. They got a lot better at
    guessing than you would expect.

## 3. Judging the candidates

Generate the full set and reject fast. Take the one that:

- **breathes** — if you can't hear an intake before a long sentence, it will sound synthetic no
  matter what else it does;
- says "Okay so —" like someone starting a thought, not like a line being read;
- goes flat on "And I mean hardest" instead of emphasising it;
- lets the pace wander between sentences rather than metronoming;
- sounds like a specific twenty-seven-year-old, not like a category.

Reject any take with: even spacing between every sentence, a lift at the end of statements,
audiobook resonance, or that faint chorus-y smoothness on held vowels.

## 4. Settings — the biggest lever after the prompt

```json
"voice": {
  "voiceId": "<paste the new voice id here>",
  "voiceName": "The Old Humans narrator - conversational American (late 20s)",
  "modelId": "eleven_v3",
  "stability": 0.3,
  "similarityBoost": 0.75,
  "wordsPerMinute": 188.3
}
```

- **`stability` is what makes it sound alive.** High stability means "say it the same way every
  time", which is the definition of robotic. The current channel is set to 0.45; drop it to
  **0.3**, or pick **Creative** in the ElevenLabs UI. The cost is the occasional odd reading —
  which is a fair trade, and re-rolling one line is cheap.
- `similarityBoost` above ~0.85 starts to sound processed. 0.75 is right.
- `speed` stays unset. Fix pace by choosing a faster voice, not by speeding up a slow one —
  time-stretched narration is audible.
- `wordsPerMinute` is measured from finished runs and overwritten automatically. Leave it.

## 5. If it still sounds synthetic

Two levers that are not the voice:

- **eleven_v3 reads inline audio tags.** A handful of `[sighs]`, `[exhales]` or `[laughs]` placed
  in the narration — not every beat, maybe four in a nine-minute script — break the even texture
  more than any setting does.
- **The script is too well-formed.** Perfectly grammatical sentences read like a document. The
  channel's sentence rules already allow fragments; a couple of "Okay, so" and one genuine
  self-interruption per script buy a lot of humanity. Keep it to that — the rules ban the rest of
  the YouTuber furniture for good reason.
