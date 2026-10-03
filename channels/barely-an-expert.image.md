# Barely An Expert: image direction

Study the actual saved pixels from https://www.youtube.com/@SomeGuyWhoKnowsStuff.
See `channels/barely-an-expert.reference.md` for the evidence order. This guide supersedes the
old single-colour-only, no-depth and no-environments rules.

## Required visual inputs

Every image generation call, including retries, replacements and provider changes, must load
the real pixels listed in `referenceStudy.imagePaths`:
- `channels/barely-an-expert.references/characters.png`
- `channels/barely-an-expert.references/scenes.png`
- `channels/barely-an-expert.references/diagrams.png`

Inspect them before drawing. Attach all three files using the provider's image-reference input
on EVERY native image generation call, including later beats and retries. A CLI image agent
must also open them with its image-view tool before generation; viewing does not replace
passing the actual files to the image tool's referenced_image_paths input.
Mentioning a filename without loading the image is not a visual reference. If the provider
cannot use the images, stop that image stage and report the missing capability. Use the
references to learn visual grammar and generate original scenes for this script.

## Drawing family

Simple, slightly imperfect black line drawings and broad flat colour regions. Match the
source's economy of line and small facial features. Avoid polished large-eyed clipart, stock
vector art, storybook painting and detailed scientific renders. Keep line weight consistent;
limbs are finer than heads and major object outlines. Do not turn every object into a sticker.

- Heads are large, white, rounded and slightly irregular. Keep returning characters consistent.
  A full standing figure generally has a head about a third of its height.
- Default eyes are tiny dots or short dashes with thin brows. Closed eyes or an eyelid stroke
  express concentration. Large black oval eyes and glossy white eyes are not the default.
  The source occasionally exaggerates eyes for a specific reaction; keep those rare.
- Mouths are small curves or simple open ovals chosen for the action, without detailed lips
  or teeth. Do not give everyone a permanent open-mouthed expression.
- Bodies and limbs are mainly single black strokes with clear bends. A plain lab coat, robe
  or other identifying garment may add a simple filled shape. Preserve the same head and limb
  language; avoid padded gloves or bulky cartoon arms.
- Hair is one simple silhouette. Keep its shape, colour and a relevant identifying prop stable
  for returning people. Historical figures are simple drawings identified by name and context,
  not realistic portraits.

## Settings and colour

The source includes flat fields, simple rooms, perspective caves, layered hills, maps and
cross-sections. A desk, ship bridge or seabed can establish a place with a few flat planes.
Shallow perspective and overlap are allowed. The background need not be an empty colour field.

Use dusty rose, brick, brown, sage, olive, blue-grey, teal, mustard, sky blue and dark navy as
the scene requires. White is normal for number cards, titles, diagrams and comparisons, and
can also be a useful clear ground. It is not restricted to a single frame type. Occasional
soft gradients, modest flat shadows or a simple glow occur in the source.

Do not cover paper with fibres, oceans with painted waves, terrain with contour texture,
planets with dozens of craters or every scene with a vignette. Detail must explain something.

## Plan scenes before drawing

Exactly one final image per script beat. A beat is a visual timing unit, not a whole sentence:
one natural spoken sentence may continue across two or three images. Typical holds average
about 4.3 seconds; final narration controls timing. Preserve punctuation and clause continuity.
Never add full stops, sentence fragments or repeated narration just to produce more images.

Plan each chapter as a developing sequence: establish a place or object, show an investigation,
move closer to the evidence, explain the mechanism, then show the consequence. Choose a relevant
subset rather than imposing that sequence mechanically. Useful source shot types include:

- A close-up reaction with a readable face.
- A person at a desk, on a ship or beside a specimen, demonstrating a specific action.
- An object or instrument close-up showing its useful parts.
- A simple setting that gives the investigation a place.
- A map, diagram or cross-section for a real spatial relationship.
- A labelled comparison or a scale comparison with a familiar object.
- Two or three people discussing or discovering evidence.
- A white number/title card at a chapter boundary without narrated filler.

Vary these deliberately. Do not generate consecutive generic ocean cross-sections, ships,
mountains and question marks merely because the topic is the ocean. When several adjacent
beats develop one diagram, visibly change the measured quantity or highlighted part; otherwise
change the action or framing. A paragraph of new information needs visual development.

Every `imagePrompt` must specify scene type, focal subject, action, framing, relevant setting
and exact optional text. "A map" is not enough. Illustrate the current beat accurately.

## Labels and speech bubbles

Use short labels for a person, instrument, quantity or contrast. Purposeful speech/thought
bubbles are part of the source: a brief confusion, observation or question can clarify a scene.
These are original illustrative lines, not invented historical quotations. Do not force one
into every image. Ordinary labels should be around 1-4 words, bubbles around 2-7 words, usually
with at most two text elements per frame. Chapter titles may be longer.

Use readable black type or simple hand lettering; light text is fine against dark backgrounds.
Specify exact text, check spelling at small preview size, and avoid narration caption bars.
Arrows and symbols must show a real relationship.

## Consistency and final checks

Compare a varied sample directly against the source pixels before the full batch: character
close-up, person in a setting, diagram and object. Check small eyes, thin limbs, head shape,
line weight and simple surfaces. An approved generated frame supplements the original sources.

Inspect completed chapter sheets for repetitive scenes, wrong instruments, illegible labels,
excessive texture and changed character design. Every provider receives the same references;
a provider switch needs a fresh sample comparison. Replace rejected images and rebuild the
MP4 after the final asset change. Verify the actual video against the final image inventory.

Canvas: full-bleed horizontal approximately 16:9. The assembled video targets 1920x1080;
save the image tool's native landscape pixels (for example 1672x941) unchanged when the art
passes review. The video assembler handles final sizing. A different native pixel size alone
is not a failed frame. Native image-generation retries are allowed to correct content or style;
keep exactly one accepted final PNG per beat and preserve rejected drafts outside the active set.
Keep key faces, objects and labels within
safe margins. No borders, mockups, watermark, channel name, logos, photographs, 3D shading or
cinematic depth of field. Depict accidents and deaths with restraint.
