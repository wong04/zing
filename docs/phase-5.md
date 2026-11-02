# Phase 5: themes

Parity rows TH1–TH5 in `PARITY.md`. The aim is to move Zing from "an accent on near-grey" to a look of its own, where colour runs through the sidebar, the toolbar, Settings and the start page.

Mock-ups: [Zing theme directions](https://claude.ai/artifact/887fwXLiVvpshHrUi2yuZt), pages "Cards, round 2" (B. Stack) and "Stack: many favourites" (B. More ribbon). The mock-ups show the intent. Where they and this document differ, this document wins.

## Why

Other browsers mostly tint the same grey window. Zing should be recognisable at a glance, without giving up a normal browser's layout or its speed. The owner liked the bolder mock-ups but worried about feasibility and speed, then chose a direction between those and a normal browser.

## Decided by the owner (2026-11-02)

- **The look is Stack.** Tabs in the sidebar are coloured cards that overlap slightly. The active card sits on top and merges into the page.
- **Favourites are hanging ribbons** along the top of the sidebar, with a "+N" ribbon that opens a panel holding the rest.
- **Dark is pastel on dark,** chosen at the hand check: light ribbons with dark text, dim cards, and a window close to black.
- **One colour, or none.** At the hand check the owner dropped the three hues. Cards and ribbons take one colour from the theme, and Settings has a Tab colours row whose None makes them neutral.
- **Light by design:** no tilted cards, and only the active card has a shadow.
- **Layout stays a normal browser's:** sidebar, toolbar, address bar and page, in their usual places.

The owner also approved these in the description of the design:

- **Ribbon count follows the sidebar's width.** "+N" appears only when they don't all fit.
- **The panel** shows icons with names. Dragging between the panel and the ribbons chooses which favourites stay visible.
- **With no favourites,** one faint outlined ribbon replaces the "Drag a tab here" box.
- **Pinned folders stay normal rows.** Only tabs become cards.
- **8–10 presets and a custom colour,** all in the Stack look, each in light and dark.
- **Sidebar on the right** mirrors the left. **Collapsed,** cards become small coloured icon chips. **Tabs on top** become folder tabs, mocked up for the owner before they're built.
- **Windows contrast themes** drop all the colour; cards become plain outlined rows.
- **No Mica.** Tried on the finished sidebar and dropped by the owner; the reason is in the README's Known differences.

## Goals

- TH1: every chrome surface takes its colour from the theme: sidebar, toolbar, Settings, overlays, menus drawn by Zing, and the start page.
- TH2: 8–10 presets in total. Ocean, Sunset, Forest, Grape, Candy, Midnight and Mono come from the plan. The builder adds one to three more.
- TH3: a custom theme from one colour, through the existing generator.
- TH4: Windows contrast themes respected; Mica dropped as above.
- TH5: switching theme never reloads a tab, closes a panel or loses a scroll position.

## Non-goals

- Sleeve-style folder cards (later, by the owner's choice) and the other mocked-up directions.
- My call, for the owner to check:
	- Pages stay as sites draw them. No recolouring websites, and no colour taken from the site.
	- Themes shared, imported or downloaded.
	- Native Windows menus. They stay as they are; this is a known limit.

## What would make this a failure

The owner's stated worries were speed and favourites running out of room, which are the middle two entries. The first and last are my call.

- A preset where text is hard to read, or a status colour that changes with the theme: a red "danger" must stay red.
- Dragging tabs feels slower or lands in the wrong place.
- A favourite that can't be reached, or that the design makes hard to tell apart.
- Settings or an overlay left in old colours after a switch.

## Constraints

- **WCAG AA stays enforced:** the existing test runs over every token pair, for every preset, in both appearances, in both vitest and xUnit through the shared vectors. Card shades and ribbon colours are included.
- **Pages are rectangular views above the chrome.** Cards, ribbons and shadows live in the sidebar and toolbar. Nothing can overlap a website except through the owned overlay windows.
- **Speed (pass rule is my call):** with 300 tabs and the finished sidebar, ribbons included, the existing test's limit of 250 ms for switching tabs stays and fails CI. Switching theme gets the same limit. Drag and scroll work is logged in CI, not failed on, because it varies too much between runs, and is compared with the table below by hand before the sidebar PR merges. Rows keep `content-visibility: auto`. No constant blur or animation.
- **Status colours stay fixed** across themes (`statusColors`).
- **Privacy:** a theme is a setting, like the accent is. Nothing about pages is stored.

## What exists

- `src/shared/theme/`: `palette.ts` (generator with contrast lift), `accent.ts` (seven families), `tokens.ts`, with a C# twin in `host/Zing.Core/Palette.cs` and vectors in `palette.vectors.json`.
- `applyAccent.ts` writes the tokens as CSS variables in the chrome (`App.tsx`) and the surface (`Surface.tsx`). `theme/tokens.css` holds the yellow defaults, so a view that doesn't call it stays yellow.
- The start page recolours in place through `StartPage.RestyleScript` when `accent` or `theme` changes (`ChromeApi.cs`).
- PR #135 forces the Appearance setting on every view, including overlays.
- `rail/`: `TabRail.tsx`, `RailRows.tsx`, `SavedSections.tsx` (`FavouriteTiles`), `useRowDrag.ts`.
- `.rail-tabs` has `overflow-x: hidden`, which stops the active card from reaching the page. That is why it fell short in the test.

## Known problems to solve

- **The merge:** the active card must reach the page edge at the rail's padding, on both sides, without breaking scrolling.
- **Row spacing during a drag:** `useRowDrag` reserves the held row's height plus 2 px. With overlapping cards the gap is too big. The landing spot was already correct in the test; the spacing isn't.
- **The rows' intrinsic size:** `contain-intrinsic-size` is 32 px and must match the card's height, or scrolling jumps.
- **Favourites can be folders.** My call: a folder ribbon is a normal ribbon showing a folder icon, and clicking it keeps opening the folder's menu.

## Mock-ups as the spec

The mock-up is the spec for how the ribbons, the "+N" ribbon and the panel look, except where this document says otherwise. The panel's contents are names with icons, as approved, rather than the mock-up's grid of letters. Its tokens are in `StackWindow.dc.html`, with `holder="more"`.

## Tabs on top

This is part of Phase 5 and has a gate: the folder-tab mock-up goes to the owner first, and that PR isn't started until they approve it. The rest of the phase doesn't wait for it.

## Left to the builder

- The setting's shape, as long as a stored `accent` keeps working and old values map to a preset. Follow `RETIRED_SETTING_KEYS` if a key is retired.
- Final colour values and the remaining preset names, subject to the AA test.
- How the panel is hosted, as long as dragging between it and the ribbons works. The plan draws it inside the sidebar (`docs/phase-5-plan.md`).
- The order of PRs. Suggested: theme model and presets, then the Stack sidebar, then ribbons and the panel, then the custom colour and contrast themes, then tabs on top once approved, with Mica last.

## Evidence so far

A throwaway test on 2026-11-02 injected Transit and Cardbox as CSS into the real sidebar, with 300 tabs, five rounds each (today's sidebar: three, after two known worker crashes).

| Style | Switching tabs (median) | Work for a full drag | Work for a full scroll |
|---|---|---|---|
| Today | 100 ms | 411 ms | 247 ms |
| Transit | 70 ms | 392 ms | 290 ms |
| Cardbox, simplified | 83 ms | 336 ms | 253 ms |
| Cardbox with tilt and shadows | 134 ms | 365 ms | 303 ms |

The differences are smaller than the variation between runs. Stack itself was not tested; simplified Cardbox is the nearest thing to it. GPU drawing was not measured, the windows were off-screen, and ribbons and preset switching weren't included.

## Verification

- `npm run test:host`, `npm test`, `npm run lint`, `npm run typecheck`, `npm run test:e2e`.
- AA test over every preset, both appearances.
- The 300-tab test repeated on the finished sidebar, and added to CI as a first step towards S10.
- e2e: switching theme keeps tabs loaded and scroll positions; every view takes the new colours; a stored `accent` still applies.
- Screenshots for each preset, light and dark, at each tab position, and split view, as `docs/PLAN.md` asks.
- By hand in a real window: dragging cards, the merge at different scales, a Windows contrast theme, and the "+N" panel.
