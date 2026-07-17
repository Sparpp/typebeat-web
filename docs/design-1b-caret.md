# Website theme: "1b Caret" (replaces neon-karaoke)

Source: claude.ai/design project "Rhythm game landing page", file "TypeBeat Landing Options.dc.html",
option **1b** ("Caret — monkeytype-lean minimal, charcoal + lime"). Imported 2026-07-17.

The whole site re-skins to this identity. **Preserve every existing page and control** — the design
mock is landing-only and omits sign-up, the full nav, reviewer buttons, etc.; those all stay, just
restyled. Do NOT drop the Sign in **and** Sign up buttons, favourite/report/download, rank/unrank,
search/filter/sort, pagination, profile sections, DMCA, etc.

## Tokens (map onto the existing CSS custom properties)

- Base bg `#141519`; raised panel `#191a20`; a slightly lifted surface `#1f2027`.
- Text `#e9eae4`; muted `rgba(233,234,228,.55)`; faint `rgba(233,234,228,.35)`; ghost `rgba(233,234,228,.25)`.
- Lines/borders `rgba(233,234,228,.10)` (and `.09` for row separators).
- **Single accent: lime `#c9f24d`** (caret, active nav, stat numbers, primary button fill, step
  labels, links). Its readable text-on-lime is the base `#141519`. No gradients, no violet/magenta.
- Semantic: keep good/warn/bad but retune to sit on charcoal — good = the lime, warn = amber `#f2c14d`,
  bad = coral `#ff6a5a`. Status pills: ranked → lime-on-dark, pending → amber, hidden → muted grey,
  removed → coral.
- Font: **JetBrains Mono everywhere** (display + body + numerals) — self-host woff2 in wwwroot/fonts
  like the outgoing Baloo 2/Nunito. Weights 400/500/600/700/800. `--font-display` and `--font-body`
  both become JetBrains Mono. Letter-spacing tight on big headings (-1 to -2px).
- Radii smaller/sharper than neon: buttons/cards ~8px (was 10–14). Minimal shadows (flat, or a thin
  border); no glow.

## Motifs

- **Blinking caret**: a lime block `width:~5px;height:~1em` with a `step-end` blink keyframe
  (`0,45%→opacity1; 50,95%→0`). Appears in the wordmark (before "type!beat") and inside the hero
  headline mid-word. Reuse one `@keyframes tbCaret` + a `.caret` class.
- Wordmark: lime caret block + `type!beat` in mono (the `!` may stay lime).
- Section/step labels like `// the rhythm game for people who type` and `01 / drop` in lime mono.

## Landing (Index.cshtml) — match 1b layout, keep our real data + links

- Centered hero: lime `//` kicker, big mono headline "type to the b|eat" with a blinking caret before
  the faded tail, muted subtitle, button row (primary lime **Download for Windows** → /download,
  ghost-bordered secondary = **Sign up** when logged out / real secondary otherwise — DO NOT drop
  sign-up), small `free · open source` meta line. Keep our live stats but render as the 1b centered
  stat bar (lime number + muted mono label): registered players / scores today / maps.
- Keep the "newest maps" strip (our real cards) below the hero — it's real functionality the mock
  lacks. Restyle cards to the Caret look via the shared card classes.
- Optional flavor: a static "now playing" demo panel (dark card, mono lyric line with caret, progress
  bar) is nice-to-have, not required; real content (stats, newest maps) takes priority.

## Nav + footer (_Layout)

- Nav: caret-block + `type!beat` wordmark (link home); center/right links Beatmaps (/beatmapsets),
  Download (/download); right side Sign in + Sign up when logged out (login link styled lime like the
  mock's "login"), or the username chip + logout when signed in. Reviewer users still reach everything.
  Keep the uptimerobot status + DMCA + source links in the footer. Footer tagline can echo the mock
  ("no circles were harmed").
- All-mono, charcoal bar, thin bottom border.

## Do-not-lose checklist (verify after re-skin)

Sign in AND Sign up (logged out); username chip + logout (logged in); Beatmaps; Download; Beatmaps
search + status filter + sort + show-more; card favourite + download + preview play; set page
favourite/report/download + leaderboard + reviewer Rank/Unrank; profile all sections; login/register
forms; /legal/dmca; styled 404/500. Theme is dark-only (matches both mock and outgoing design).
