# type!beat web style guide — "neon karaoke"

The single stylesheet is `src/Typebeat.Web/wwwroot/css/site.css`. Everything below is defined
there as CSS custom properties (tokens) and plain component classes — no build step, no JS
framework. Dark theme only (`color-scheme: dark`); there is no light variant and pages must not
assume one.

Identity in one line: **deep indigo/near-black base, violet-first accent with a hot-magenta
gradient tail, rounded sans type (Baloo 2 display / Nunito body), subtle glow.**

## Tokens

### Surfaces

| Token | Value | Use |
|---|---|---|
| `--bg` | `#0c0a16` | page background (deep indigo, never pure black) |
| `--bg-raised` | `#131024` | nav/footer bands, inputs, table headers |
| `--surface` | `#181430` | cards, panels |
| `--surface-2` | `#201a40` | nested/hover surfaces, neutral pill bg |
| `--line` | `#2b2450` | default borders |
| `--line-bright` | `#3d3470` | interactive borders (inputs, ghost buttons) |

### Text

| Token | Value | Use |
|---|---|---|
| `--text` | `#ece9ff` | primary text |
| `--text-muted` | `#9b93c4` | secondary text, nav links |
| `--text-faint` | `#6c6597` | labels, copyright, hints |

### Accents

| Token | Value | Use |
|---|---|---|
| `--violet` | `#8d5bf7` | THE brand accent; hovers, focus, highlights |
| `--violet-deep` | `#7a3ff2` | gradient head |
| `--violet-bright` | `#a678ff` | link color, accent text on dark |
| `--magenta` | `#e84baf` | gradient tail ONLY — never a standalone fill |
| `--good` / `--warn` / `--bad` | green/amber/red | semantic states |
| `--grad-primary` | violet → magenta, 115° | primary buttons, wordmark |
| `--glow-violet`, `--glow-magenta` | soft box-shadows | hover/focus glow accents |

The gradient is violet-dominant by construction (magenta enters at the 100% stop). Keep it that
way: **violet leads, magenta seasons**.

### Type

- `--font-display`: `'Baloo 2'` — headings, wordmark, buttons, numerals in chips.
- `--font-body`: `'Nunito'` — everything else.
- Scale: `--text-xs` (.75rem) → `--text-4xl` (fluid `clamp(2.5rem, 6vw, 3.5rem)`).
- Both fonts are self-hosted variable woff2 (latin subset) in `wwwroot/fonts/`, OFL-licensed
  (see `OFL-NOTICE.txt` there).

### Radii & spacing

- `--radius-sm/md/lg` = 6/10/16px, `--radius-full` = pill.
- `--space-1..8` = 4/8/12/16/24/32/48/64px. Use tokens, not magic numbers.

## Component classes

| Class | What it is |
|---|---|
| `.container` | centered 1080px max-width wrapper with side padding |
| `.site-nav` + `.nav-inner` | sticky translucent top bar; flex-wrap, no JS |
| `.wordmark` | gradient-text "type!beat" logo type (display font) |
| `.nav-links` | middle nav link group |
| `.nav-auth` | right-aligned auth area (`margin-left:auto`) |
| `.user-chip` | signed-in username pill (links to profile) |
| `.site-footer` + `.footer-inner` + `.footer-copy` | footer band; copy right-aligned |
| `.btn` | base button (pill shape, display font); combine with a variant |
| `.btn-primary` | gradient fill, white text, glow on hover |
| `.btn-ghost` | transparent, `--line-bright` border, violet glow on hover |
| `.btn-small` / `.btn-big` | size modifiers |
| `.card` (+ `.card-title`) | surface panel, `--radius-lg` |
| `.pill` | status pill base (uppercase, tiny); variants `.pill--public`, `.pill--hidden`, `.pill--removed`, `.pill--accent` |
| `.chip` (+ `.chip-label`, `.chip-value`) | stat chip: faint label + bold display-font value |
| `.field` (+ `label`, `.input`, `.field-error`, `.field-hint`) | form group |
| `.alert-error` | top-of-form error banner |
| `.table-wrap` > `.table` | horizontally-scrollable styled table (leaderboards) |
| `.hero` (+ `.hero-title`, `.hero-sub`, `.hero-actions`, `.hero-note`) | landing hero block |
| `.auth-card` | narrow (420px) centered card for login/register |
| `.card-note` | small muted footnote line at the bottom of a card |
| `.prose` | 68ch max-width column for long-form text pages |
| `.muted` / `.faint` / `.text-center` / `.stack` | tiny helpers |

Pattern for new pages: compose these; add page-specific classes to `site.css` under a clearly
commented section rather than inline styles.

## Do NOT (binding — trade dress, see recon `result.web.trade_dress_warnings.txt`)

1. **No osu! pink `#FF66AA`** or any hue-rotating theme system. Our anchor hue is violet and it
   is fixed.
2. **No flat pink-on-black**: magenta never appears as a standalone fill/background — only as
   the tail of `--grad-primary` or a faint glow.
3. **No triangles motif** — no drifting-triangle textures in buttons, banners, or panels. Flat
   fills, gradients, or glyph/cursor motifs only.
4. **No Torus font or lookalikes**; do not bundle osu's brand face. We ship Baloo 2 + Nunito.
5. **No osu! mascots, logos, roundels, grade-badge/medal artwork**, and no osu slogans or
   microcopy ("free-to-win", "rhythm is just a click away", "ppy powered", etc.). Write our own
   voice.
6. **No osu-hosted assets** (covers, previews, news images) — copyright, not just trade dress.
7. **No pasted osu-web code** (AGPL). Port layouts as specs only.
8. Litmus test: a screenshot with the logo cropped must NOT be mistakable for osu.ppy.sh.

## Auth/session facts Phase B pages can rely on

- `_Layout.cshtml` renders nav + footer around `RenderBody()`; set `ViewData["Title"]`.
- Page models inherit `TypebeatPageModel` → `CurrentUser` (`AuthedUser?`).
- In views, `Context.SessionUser()` (extension in `Typebeat.Web.Auth`) gives the same value.
- Every state-changing `<form method="post">` needs an antiforgery token: tag-helper forms
  (`asp-page=...`) inject it automatically; plain forms must call `@Html.AntiForgeryToken()`.
