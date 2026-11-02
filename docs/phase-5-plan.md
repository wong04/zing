# Phase 5 Themes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Zing the Stack look (coloured overlapping tab cards, hanging favourite ribbons) across eight presets and a custom colour, in light and dark.

**Architecture:** One new pure function, `stackTokens`, derives the Stack colours from the same hue and saturation as today's palette, in TS and in C#. Every view writes them as CSS variables, so a theme switch is only a variable change. The sidebar's look is CSS over the existing rows. The only new logic is `ribbonLayout` and the favourites panel, which lives inside the sidebar.

**Tech Stack:** TypeScript, React, Vitest, Playwright over CDP; C#, xUnit, WebView2.

**Spec:** `docs/phase-5.md`. Mock-up: `StackWindow.dc.html` with `holder="more"` in the [Zing theme directions](https://claude.ai/artifact/887fwXLiVvpshHrUi2yuZt) artifact.

## Global Constraints

- Tabs for indentation; no fallbacks; raise errors explicitly. Match the surrounding code.
- WCAG AA over every token pair, every preset, both appearances, in vitest and xUnit through shared vectors.
- `statusColors` never change with the theme.
- Rows keep `content-visibility: auto`. No constant blur or animation. Only the active card has a shadow; no tilted cards.
- Nothing draws over a website except the existing overlay windows. The page stays a rectangle.
- Test runs stay off-screen with no focus or dialogs (`launchZing` does this). Never start Zing by hand without `ZING_E2E=1` and a scratch `ZING_USER_DATA`.
- Before each PR: `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:host`, `npm run test:e2e`.
- One branch and PR per group below. Commit messages short and lowercase, ending with the session's attribution line. Merge only when the owner says so.

## Decisions this plan makes

For the owner to check:

- **No settings migration.** The `accent` key and its stored ids stay; only the labels change (`blue` shows as Ocean). A stored file therefore keeps working with no code. New ids: `midnight`, `custom`. New key: `customColor`.
- **Eight presets:** Amber (`yellow`, today's look, the one addition to the plan's seven), Ocean (`blue`), Sunset (`red`), Forest (`green`), Grape (`purple`), Candy (`magenta`), Midnight (`midnight`), Mono (`mono`).
- **The "+N" panel is drawn inside the sidebar, not floating over the page as in the mock-up.** An overlay window is a separate web view, and dragging a favourite between it and the ribbons would need routing across views. Inside the sidebar the existing drag works unchanged. It shows a list of icons with names, over the tabs.
- **The sidebar's scrollbar goes away** (`scrollbar-width: none`, as the tab strip and app sidebar already do), because it would sit between the active card and the page. The wheel and keyboard still scroll.
- **The active card's colour is the new `--page` token,** and the start page uses it as its background, so the merge is exact on new tabs. On a website it can't match the site's own colour; a seam against a dark site in light mode is expected.
- **Which favourites show is just their order.** The first ones are ribbons, the rest are in the panel. Dragging reorders Favourites, so no new stored state.

## Review Focus

1. A custom colour of pure black, white, grey or yellow: every pair stays legible and the active card stays distinct from the others (Task 1).
2. An existing settings file with `accent: "red"` and no `customColor`: it loads, and shows Sunset (Task 3).
3. The active favourite is hidden in the panel: the "+N" ribbon shows that it holds the active one (Task 9).
4. The panel is open and the user clicks the page, which is another web view: the panel closes (Task 9).
5. Sidebar at its minimum width of 160, or collapsed, with many favourites: every favourite is still reachable (Tasks 8 and 9).

---

## PR 1: theme model

### Task 1: Presets and Stack tokens in TS

**Files:**
- Modify: `src/shared/theme/accent.ts`, `src/shared/theme/palette.ts`, `src/shared/theme/color.ts`
- Test: `src/shared/theme/theme.test.ts`

**Interfaces:**
- Produces:
	- `CUSTOM_ACCENT = 'custom'`
	- `seedProfile(hex: string): HueProfile` in `accent.ts`
	- `themeProfile(accent: string, customColor: string): HueProfile`, throwing `RangeError` on an unknown accent
	- `themeTokens(accent: string, customColor: string, dark: boolean): PaletteTokens`
	- `stackTokens(profile: HueProfile, dark: boolean): StackTokens` in `palette.ts`
	- `StackTokens = { window, windowInk, band, bandInk, field, fieldInk, page, pageInk, cards: [string, string, string], cardInks: [...], ribbons: [...], ribbonInks: [...], more, moreInk }`

- [ ] **Step 1: Write the failing tests** in `theme.test.ts`:
	- Change the family count to 8, and the "covers" test to `8 families × 2 modes × 9 pairs` = 144.
	- `describe('stack tokens are legible')`: for every family plus seeds `#000000 #FFFFFF #808080 #FFFF00 #3366CC`, both modes, `contrastRatio >= wcagAA` for windowInk/window, bandInk/band, fieldInk/field, pageInk/page, each cardInks[i]/cards[i], ribbonInks[i]/ribbons[i], moreInk/more.
	- `it('the active card stands out')`: same matrix, `contrastRatio(page, cards[i]) >= 1.1`.
	- `it('a seed takes its hue')`: `seedProfile('#3366CC').hue` within 1 of 220; `seedProfile('#000000').saturation` is 0.07.
	- `it('refuses a bad seed')`: `seedProfile('blue')` throws `InvalidHexColorError`.
	- `it('labels are the preset names')`: `accentLabel` of `blue` is `Ocean`, `yellow` is `Amber`, `midnight` is `Midnight`.
- [ ] **Step 2: Run `npx vitest run src/shared/theme/theme.test.ts`.** Expected: FAIL on the missing exports.
- [ ] **Step 3: Implement.**
	- `AccentTheme` gains `midnight` with `{ hue: 232, saturation: 0.7 }`; `red` moves to hue 14. Labels: yellow Amber, green Forest, blue Ocean, magenta Candy, purple Grape, red Sunset, mono Mono, midnight Midnight.
	- `seedProfile`: hue and HSL saturation from the hex, saturation clamped to 0.07…1. Add `hslFromHex(hex): { hue, saturation, lightness }` to `color.ts`.
	- `stackTokens`, with `S = profile.saturation * 0.75`, `H = profile.hue`, using the mock-up's formulas:

```ts
const window = dark ? hexFromHSL(H, 0.4 * S, 0.15) : hexFromHSL(H, 0.55 * S, 0.82)
const page = dark ? hexFromHSL(H, 0.2 * S, 0.085) : hexFromHSL(H, 0.3 * S, 0.99)
const band = mix(window, dark ? '#000000' : hexFromHSL(H, 0.6 * S, 0.3), dark ? 0.3 : 0.22)
const field = mix(band, '#FFFFFF', dark ? 0.1 : 0.62)
const paint = (h: number) => hexFromHSL(h, 0.75 * S, dark ? 0.42 : 0.6)
const hues = [H, H + 45, H - 45]
const ribbons = hues.map(paint)
const cards = hues.map((h) => mix(window, paint(h), 0.62))
const more = mix(window, inkOn(window), 0.78)
```

	- `inkOn(bg)` returns whichever of `#000000` and `#FFFFFF` has the higher contrast. Pure black and white guarantee at least 4.58:1 on any background, so no lifting is needed.
- [ ] **Step 4: Run the same command.** Expected: PASS.
- [ ] **Step 5: Commit.** `feat: stack tokens and preset names`

### Task 2: C# port and start page

**Files:**
- Modify: `host/Zing.Core/Palette.cs`, `host/Zing.Core/StartPage.cs`, `host/Zing.Core/ChromeApi.cs:523`, `src/shared/theme/palette.vectors.json`, `src/shared/theme/palette.test.ts`
- Test: `host/Zing.Core.Tests/SharedVectorTests.cs`, `host/Zing.Core.Tests/StartPageTests.cs`

**Interfaces:**
- Consumes: Task 1's functions.
- Produces:
	- C# `record StackTokens(...)` with the same fields
	- `Palette.ThemeProfile(string accent, string customColor)`, `Palette.ThemeTokens(accent, customColor, dark)`, `Palette.Stack(profile, dark)`, `Palette.SeedProfile(hex)`
	- `StartPage.Theme(string accent, string customColor)`

- [ ] **Step 1: Extend the vectors.** With a temporary vitest case, write a `stack` object into every `accents` entry and a new `seeds` section (the five seeds of Task 1, both modes, with `tokens` and `stack`), then delete the case. `palette.test.ts` asserts both sections.
- [ ] **Step 2: Add xUnit tests.** `Stack_matches_the_TS` and `Seed_matches_the_TS` over the vectors. In `StartPageTests.cs`, extend the AA theory with the Task 1 pairs for every family and seed. Add `Theme_for_a_custom_colour_uses_the_seed`, and `The_start_page_background_is_the_page_token`.
- [ ] **Step 3: Run `npm run test:host`.** Expected: FAIL to compile.
- [ ] **Step 4: Implement the port.** `Palette.Accents` gains `midnight`; `red` becomes `(14, 0.84)`. `StartPage.Tokens` takes the stack tokens and sets `Background` to `Page`. `StartPage.Html` reads `customColor`. `ChromeApi.cs:523` restyles on `accent`, `theme` or `customColor`. Throw `UnknownAccentException` as today.
- [ ] **Step 5: Run `npm run test:host` and `npm test`.** Expected: PASS.
- [ ] **Step 6: Commit.** `feat: port stack tokens to the host`

### Task 3: Settings and CSS variables in every view

**Files:**
- Modify: `src/shared/settings/schema.ts`, `src/shared/settings/settings.vectors.json`, `scripts/host-defaults.ts`, `host/Zing.Core/Settings.cs`, `src/renderer/src/settings/applyAccent.ts`, `src/renderer/src/settings/useAccentTheme.ts`, `src/renderer/src/theme/tokens.css`, `src/renderer/src/overlay/Overlay.tsx`, `src/renderer/src/overlay/PageUi.tsx`
- Test: `src/renderer/src/settings/applyAccent.test.ts`, `host/Zing.Core.Tests/SettingsTests.cs`, new `e2e/themes.spec.ts`

**Interfaces:**
- Consumes: `themeTokens`, `themeProfile`, `stackTokens`.
- Produces:
	- `accentVariables(accent: string, customColor: string, dark: boolean): Record<string, string>`, adding `--window --window-ink --band --band-ink --field --field-ink --page --page-ink --card-1..3 --card-ink-1..3 --ribbon-1..3 --ribbon-ink-1..3 --more --more-ink`
	- `applyAccent(accent, customColor)`
	- settings `accent` also accepts `midnight` and `custom`; `customColor` defaults to `#D9B872`
	- `hexColor` rule type, on both sides

- [ ] **Step 1: Write the failing tests.**
	- `applyAccent.test.ts`: update existing calls; `accentVariables('custom', '#3366CC', false)` differs from yellow; `it('status colours are not themed')`: no returned key is `--positive`, `--caution` or `--danger`; `it('tokens.css holds the yellow values')` parses `tokens.css` and compares every variable, for both modes, against `accentVariables('yellow', '#D9B872', dark)`, so the defaults can't drift.
	- `SettingsTests.cs`: `accent = "red"` with no `customColor` loads and defaults the colour; `customColor = "blue"` is refused with `InvalidSettingException`.
	- `e2e/themes.spec.ts`, `every view takes the theme`: set `accent` to `blue`, then open Settings, a notice and find, and assert each view's `--accent` equals `accentVariables('blue', …)`. It must fail today for `overlay` and `pageui`, which never call `useAccentTheme`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Add the `hexColor` rule (`/^#[0-9a-fA-F]{6}$/`) in `schema.ts`, `SETTING_RULES` and `SettingsSchema.Accepts`, then run `npm run gen:host-defaults`. Call `useAccentTheme()` in `Overlay` and `PageUi`. Add the new variables to `tokens.css`.
- [ ] **Step 4: Run `npm test`, `npm run test:host` and `npx playwright test e2e/themes.spec.ts` after `npm run build && npm run build:host`.** Expected: PASS.
- [ ] **Step 5: Commit.** `feat: theme tokens in every view`

### Task 4: Colour on the chrome surfaces

**Files:**
- Modify: the CSS holding `.toolbar` and the address field, `src/renderer/src/settings/settings.css`, `src/renderer/src/settings/CustomRows.tsx`, `src/renderer/src/settings/rows.ts`
- Test: `e2e/themes.spec.ts`

**Interfaces:**
- Consumes: Task 3's variables.

- [ ] **Step 1: Add the failing e2e test** `the toolbar and Settings carry the theme`: the toolbar's computed background equals `--band`, the address field's equals `--field`, Settings' header equals `--band`. The preset picker lists eight radios named by preset and no `custom` yet.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement.** Toolbar uses `--band` and `--band-ink`; the address field uses `--field` and `--field-ink`; Settings' header uses `--band`. The picker's title becomes "Theme" and it filters out `custom`. Run `rg -n "#[0-9a-fA-F]{3,8}\b" src/renderer/src --glob "*.{css,tsx}"` and move any colour outside `tokens.css` onto a token, unless a comment says it's fixed on purpose.
- [ ] **Step 4: Run the test.** Expected: PASS.
- [ ] **Step 5: Commit and open PR 1.** `feat: theme colour on the toolbar and settings`

---

## PR 2: Stack sidebar

### Task 5: Cards that overlap and merge

**Files:**
- Modify: `src/renderer/src/rail/rail.css`
- Test: `e2e/vertical-tabs.spec.ts`

- [ ] **Step 1: Write the failing e2e tests.**
	- `tab rows are overlapping cards`: with five tabs, a row's height is 38, consecutive tops differ by 30, and non-active fills repeat with period 3 and match `--card-1..3`.
	- `the active card reaches the page`: the active row's right edge equals the page's left edge from `layers()`, its background equals `--page`, and only it has a `box-shadow`.
	- `the tab list still scrolls with the active card merged`: with 60 tabs, a wheel over the list changes `scrollTop`, and `scrollWidth` equals `clientWidth`.
	- `switching tabs doesn't move the others`: rows' tops are the same before and after a switch.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement in `rail.css`, using the mock-up's values.**
	- `.rail`: `background: var(--window); color: var(--window-ink)`. Drop the pinned rail's border; the colour change is the edge.
	- `.rail-tabs`: `gap: 0; margin-right: -8px; padding-right: 8px; scrollbar-width: none` (mirrored on the right in Task 7). Add `padding-top: 4px` so the active card's shadow isn't clipped.
	- Shades without per-row work: `.rail-tabs > :nth-child(3n+1) { --card: var(--card-1); --card-ink: var(--card-ink-1) }`, and the same for 3n+2 and 3n.
	- `.rail-tab`: `height: 38px; margin-bottom: -8px; align-items: flex-start; padding-top: 6px; border-radius: 8px 8px 0 0; background: var(--card); color: var(--card-ink); border-top: 1px solid color-mix(in srgb, var(--card) 80%, var(--card-ink))`.
	- `.rail.pinned .rail-tab.active`: `position: relative; z-index: 1; height: 42px; margin-block: -2px -10px; margin-right: -8px; border-radius: 10px 0 0 10px; border-top: 0; background: var(--page); color: var(--page-ink); font-weight: 600; box-shadow: 0 -3px 8px rgb(0 0 0 / 0.2)`.
	- `contain-intrinsic-size: auto 38px`.
	- The group head stays a plain row with `--window-ink`; nested tabs are cards, with their `nth-child` shade set inside `.rail-group`.
- [ ] **Step 4: Run them, then all of `e2e/vertical-tabs.spec.ts` and `e2e/saved-tabs.spec.ts`.** Expected: PASS.
- [ ] **Step 5: Commit.** `feat: stack cards in the sidebar`

### Task 6: Drag spacing for overlapping cards

**Files:**
- Create: `src/renderer/src/rail/dragRoom.ts`, `src/renderer/src/rail/dragRoom.test.ts`
- Modify: `src/renderer/src/rail/useRowDrag.ts:92`
- Test: `e2e/vertical-tabs.spec.ts`

**Interfaces:**
- Produces: `dragRoom(keys: readonly string[], held: string, frames: ReadonlyMap<string, { top: number; height: number }>): number`

- [ ] **Step 1: Write the failing tests.** `dragRoom` is the distance from the held row's top to the next row's top; for the last row, from the previous row's top; for a single row, its height. E2E `a dragged card leaves a gap of one card and lands where it shows`: at mid-drag the neighbour has moved by 30 px, and after release the order is correct.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement,** and use it at `useRowDrag.ts:92` in place of `height + 2`.
- [ ] **Step 4: Run them, plus `npm run test:e2e:real-input` if the runner allows.** Expected: PASS.
- [ ] **Step 5: Commit.** `fix: drag room follows the row spacing`

### Task 7: Right side, collapsed, floating and split

**Files:**
- Modify: `src/renderer/src/rail/rail.css`
- Test: `e2e/vertical-tabs.spec.ts`, `e2e/split.spec.ts`

- [ ] **Step 1: Write the failing e2e tests.**
	- `on the right the active card merges leftward`: its left edge equals the page's right edge, radius `0 10px 10px 0`.
	- `collapsed, cards are icon chips`: rows are 36 by 32, no overlap, fills still `--card-n`, titles hidden, the active chip is `--page` with no merge.
	- `the floating rail's active card stays inside it`: radius on all corners, right edge inside the rail.
	- `split tabs don't overlap`: inside `.rail-split`, rows have no negative margin and the active one stays inside the box.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Mirror margins, padding and radius under `.rail.pinned.right`. Under `.rail.collapsed` and `.rail-split`, reset the overlap and merge; `.rail-split` takes `--card` from its `nth-child` and its rows inherit it.
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit and open PR 2.** `feat: stack cards on every side`

---

## PR 3: ribbons and panel

### Task 8: Ribbons

**Files:**
- Create: `src/shared/layout/ribbons.ts`, `src/shared/layout/ribbons.test.ts`
- Modify: `src/renderer/src/rail/SavedSections.tsx`, `src/renderer/src/rail/TabRail.tsx`, `src/renderer/src/rail/rail.css`
- Test: `e2e/saved-tabs.spec.ts`

**Interfaces:**
- Produces: `ribbonLayout(count: number, innerWidth: number): { shown: number; more: number }`; `FavouriteRibbons({ nodes, frame, innerWidth })`, replacing `FavouriteTiles`.

- [ ] **Step 1: Write the failing tests.** `ribbonLayout` with ribbons 40 wide and gaps of 4, so `slots = max(1, floor((innerWidth + 4) / 44))`:

```ts
it.each([
	[16, 224, 4, 12], [5, 224, 5, 0], [6, 224, 4, 2], [4, 144, 2, 2],
	[3, 144, 3, 0], [2, 36, 0, 2], [1, 36, 1, 0], [0, 224, 0, 0],
])('%i favourites in %i px', (count, width, shown, more) => {
	expect(ribbonLayout(count, width)).toEqual({ shown, more })
})
```

	E2E:
	- `favourites are ribbons with a count`: with 16 favourites at width 240, four ribbons and a button named "12 more favourites".
	- `no +N when they all fit`.
	- `resizing changes the count`: at width 160, two ribbons and "+N".
	- `an empty Favourites is one outlined ribbon`: `.rail-ribbon.empty` with title "Drag a tab here, or press Ctrl+D", no `.rail-hint` inside Favourites; dragging a tab onto it saves the tab.
	- `a folder ribbon opens its menu`: shows the folder icon; clicking it still calls the folder menu.
	- Update existing `rail-tile` and hint selectors in `e2e/saved-tabs.spec.ts`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
	- `TabRail` passes `innerWidth`: `width.value - 16`, or `VERTICAL_TABS.collapsedWidth - 12` when collapsed.
	- Ribbons keep today's `data-drop-*` attributes, drag handlers, active and unloaded classes. Each is `.rail-ribbon`: width 40, height 54, `display: flex; align-items: flex-end; justify-content: center; padding-bottom: 13px; clip-path: polygon(0 0, 100% 0, 100% 100%, 50% 86%, 0 100%)`, colour from `--ribbon-n` by `nth-child(3n+…)`.
	- An active ribbon adds a 3 px `--page` bar along its top edge, since `clip-path` hides any outline or shadow.
	- `.rail-favourites`: `display: flex; gap: 4px; position: relative`. Drop the 52 px grid and the `.rail-tile` rules.
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit.** `feat: favourites as ribbons`

### Task 9: The "+N" panel

**Files:**
- Create: `src/renderer/src/rail/FavouritesPanel.tsx`
- Modify: `src/renderer/src/rail/SavedSections.tsx`, `src/renderer/src/rail/rail.css`
- Test: `e2e/saved-tabs.spec.ts`

**Interfaces:**
- Consumes: `ribbonLayout`, `SavedFrame`, `ItemIcon` and `open` (exported from `SavedSections.tsx`).
- Produces: `FavouritesPanel({ nodes, firstIndex, frame, onClose })`.

- [ ] **Step 1: Write the failing e2e tests.**
	- `the panel lists the rest with names`: clicking "+12" shows 12 rows with titles in `aria-expanded` state, each opening its favourite.
	- `a favourite dragged from the panel onto a ribbon becomes visible`: the order changes, the last ribbon moves into the panel, and the count stays 12.
	- `a ribbon dragged into the panel leaves the ribbons`.
	- `the panel closes on Escape, returning focus to the ribbon, and when the chrome loses focus`: dispatch `blur` on `window` for the page-click case.
	- `+N shows when it holds the active favourite`: `.rail-ribbon.more.holds-active`.
	- `collapsed, the panel is an icon column with tooltips, and all favourites are reachable`.
	- `Ctrl+D with a full row raises the count`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
	- "+N" is a `<button aria-expanded aria-controls>` with `--more` fill, `data-drop-item`, and `data-drop-index={shown}`, so a dragged tab or favourite can land on it.
	- Open state is `useState` in `FavouriteRibbons`.
	- The panel is `position: absolute; top: 100%; left: 0; right: 0; z-index: 6; max-height: 60vh; overflow-y: auto; background: var(--page); color: var(--page-ink); border-radius: 10px; padding: 10px; box-shadow: 0 8px 26px rgb(0 0 0 / 0.3)`.
	- The header says "12 more favourites". Rows are `data-drop-parent={FAVOURITES_ID}`, `data-drop-index={firstIndex + i}`, `data-drop-axis="y"`, and the panel root has `data-drop-folder={FAVOURITES_ID}`. Folder rows open the folder menu.
	- Close on Escape and on window `blur`, but not during a drag.
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit and open PR 3.** `feat: the more ribbon and its panel`

---

## PR 4: custom colour, contrast themes, switching and proof

### Task 10: Custom colour in Settings

**Files:**
- Modify: `src/renderer/src/settings/CustomRows.tsx`, `src/renderer/src/settings/settings.css`
- Test: `e2e/themes.spec.ts`

- [ ] **Step 1: Write the failing e2e test** `a custom colour themes everything`: a ninth radio "Custom" reveals an `<input type="color">`; setting `#3366CC` stores `accent: custom` and `customColor`, and the rail, the start page and Settings all change; restarting with the same user data keeps it.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement.** The radio's swatch shows `customColor`; the input is shown only while Custom is selected and calls `handle.set('customColor', value)` on `change`, not on `input`, so dragging in the colour popup doesn't restyle on every step.
- [ ] **Step 4: Run it.** Expected: PASS.
- [ ] **Step 5: Commit.** `feat: custom theme colour`

### Task 11: Windows contrast themes

**Files:**
- Modify: `src/renderer/src/rail/rail.css`, `src/renderer/src/chrome/tabs/tabs.css`
- Test: `e2e/themes.spec.ts`

- [ ] **Step 1: Write the failing e2e test** `in a contrast theme cards are plain outlined rows`: with `page.emulateMedia({ forcedColors: 'active' })`, a tab row has no negative margin, a 1 px border and 32 px height; a ribbon has `clip-path: none` and a border; the active card has no shadow.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement** a `@media (forced-colors: active)` block in `rail.css`: reset the overlap and merge, `border: 1px solid CanvasText`, active `border-color: Highlight`, ribbons `clip-path: none; height: 32px`.
- [ ] **Step 4: Run it.** Expected: PASS. A real contrast theme is covered by Task 12's hand check.
- [ ] **Step 5: Commit.** `feat: stack look in contrast themes`

### Task 12: Switching keeps everything, speed, screenshots

**Files:**
- Create: `e2e/theme-speed.spec.ts`, `e2e/theme-screenshots.spec.ts`
- Modify: `e2e/themes.spec.ts`

- [ ] **Step 1: Write the switching test** `switching theme keeps everything` in `themes.spec.ts`: scroll a long page and record `scrollY`, a navigation counter from `window.name`, tab count, open Settings and a tool panel. Switch through all eight presets. Everything is unchanged, Settings and the panel are still open, and the start page tab's `--accent` changed with no reload.
- [ ] **Step 2: Write `e2e/theme-speed.spec.ts`.** With `add-tabs` of 300 and the rail at default width with 16 favourites:
	- Median of seven tab switches `< 250`.
	- Median of seven theme switches `< 250`, measured from `set` until the chrome's `--accent` shows the new value and a `requestAnimationFrame` has run.
	- Log drag and scroll times (long task totals, as in the earlier test) with `test.info().annotations`, without asserting them.
	- CI's existing e2e job picks this up with no workflow change.
- [ ] **Step 3: Run both.** Expected: PASS. Compare the logged drag and scroll with the table in `docs/phase-5.md` and write the numbers into the PR body. If switching exceeds its limit, stop and report it; don't raise the limit.
- [ ] **Step 4: Write `e2e/theme-screenshots.spec.ts`,** skipped unless `ZING_SCREENSHOTS=1`: for each preset, each of top, left and right, light and dark, with a split open and the "+N" panel closed, one `chrome` screenshot to `docs/screenshots/phase-5/<preset>-<position>-<mode>.png`. Run it. Expected: 48 files. Tabs on top will show the old strip until PR 5, so rerun it then.
- [ ] **Step 5: Hand check with the owner,** because off-screen tests can't judge these. Using the run copy and `relaunch.ps1`:
	- dragging cards
	- the merge at 100%, 125% and 150% scale
	- a Windows contrast theme
	- the "+N" panel over a page
	- a custom colour in the popup
- [ ] **Step 6: Commit and open PR 4.** `test: theme switching, speed and screenshots`

---

## PR 5: tabs on top (gated)

### Task 13: Mock-up for the owner

**Files:**
- Modify: artifact `StackWindow.dc.html`, new artboard `StackTop`.

- [ ] **Step 1: Add a `StackTop` page** with folder tabs in the tab strip: the same three shades, the active tab merging down into the toolbar, ribbons in the favourites bar, light and dark. Publish it.
- [ ] **Step 2: Stop and ask the owner.** Nothing below starts until they approve. Record any changes in `docs/phase-5.md`.

### Task 14: Folder tabs

**Files:**
- Modify: `src/renderer/src/chrome/tabs/tabs.css`, `src/renderer/src/library/FavouritesBar.tsx` if the approved mock-up changes the bar
- Test: `e2e/tabs.spec.ts`

- [ ] **Step 1: Write the failing e2e tests,** using the approved mock-up's values: `.tab` fills repeat `--card-1..3`; the active tab's background is `--band` and its bottom edge meets the toolbar's top; drag still lands correctly. Add these rules to the forced-colors block.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** in `tabs.css`; `useSlotDrag` measures positions at pickup, so only check it, don't change it.
- [ ] **Step 4: Run them, then rerun the screenshots for `top`.** Expected: PASS.
- [ ] **Step 5: Commit and open PR 5.** `feat: folder tabs on top`

---

## PR 6: Mica and wrap-up

### Task 15: Resolve Mica, close the rows

**Files:**
- Modify: `host/Zing.Host/HostedView.cs:127`, `src/renderer/src/rail/rail.css` (only if Mica stays), `README.md:36`, `PARITY.md`

- [ ] **Step 1: Try one visible area.** Make the chrome view transparent (`DefaultBackgroundColor`, as overlays do) and `.rail-head` `background: transparent`. Rerun `e2e/theme-speed.spec.ts`.
- [ ] **Step 2: Check it by eye with the owner,** because capture tools can't see WebView2 pixels. Keep it only if the Mica shows and the speed test still passes.
- [ ] **Step 3: If it doesn't stay, revert Step 1 and add to README's Known differences:** "**Mica.** Solid tab cards cover the window, so Windows' Mica material isn't visible behind Zing's sidebar." Either way, note the result in `PARITY.md`.
- [ ] **Step 4: Update `PARITY.md`:** TH1–TH5 to Done, or TH4 to Partial if Mica was dropped and the owner counts that as partial. S10 stays Open; add a note that the 300-tab test is its first step.
- [ ] **Step 5: Run the whole suite from Global Constraints.** Expected: all pass (rerun on the known Playwright worker crash, exit 3221226505).
- [ ] **Step 6: Commit and open PR 6.** `docs: close the theme parity rows`
