# Phase 4: tab bar position + split view

Parity rows T1–T5 in `PARITY.md`, plus two new rows: T6 (favourite tiles) and T7 (pinned items). The visual design follows the existing tokens; the colour refresh is Phase 5.

## Decisions (2026-11-01)

- **Tabs go on the left by default.** `tabBarPosition` stays a setting (top, left or right), and its default changes from `top` to `left`. Someone who chose a position keeps it.
- **The sidebar follows Arc and Dia.** Favourite tiles sit at the top. Pinned items come next, in folders, much like a bookmark bar. Then a divider, New Tab, and the tabs opened today.
- **Favourites and pinned items are saved tabs.**
  - Each item owns at most one tab per window. Clicking an item switches to its tab, or opens it.
  - Closing that tab unloads it, but the item stays.
  - A tab that has moved off its item's address can be reset to it, or the item can be updated to the current page.
  - Items are saved on disk, as bookmarks already are. Their pages are not: an item's tab always starts fresh, so Zing still keeps no browsing data.
- **The favourite tiles are the Ctrl+D Favourites folder.** With tabs on the side, the top Favourites bar goes away, because the tiles replace it.
- **Split view comes after the sidebar,** in its own PR.

## Foundation (done)

- `src/shared/layout/chrome.ts`:
  - `chromeInsets()` gives the page's insets for each tab position.
  - `VERTICAL_TABS` holds the rail's default width (240), minimum (160), maximum (480) and collapsed width (48).
  - `clampRailWidth()` keeps the width within those limits.
- `Layout.Insets` (host) already places the page beside a rail for `left` and `right`. The app sidebar takes no column of its own there.
- `TabReorder` (host) already takes an `Axis`; `Axis.Y` has no caller yet.
- The Settings row for Top, Left and Right exists. No renderer code reads the setting yet.
- `src/shared/layout/split.ts` (Phase 2) holds the split layouts, used in part C.

## Work

### A. The rail (T1, T2, T3), PR 1

- **Layout.** With tabs on the side, the tab strip row goes. The toolbar becomes the window's top band, with the drag region and the room for the caption buttons. The rail is a column on its side. The Favourites bar stays under the toolbar until PR 2 turns the Favourites into tiles.
- **What the rail shows, top to bottom:**
  - the app sidebar's items: Browser, Toolbox, recents and Settings, in one compact row (T3)
  - a divider and New Tab
  - today's tabs as a vertical list, each row showing an icon and a title, with close on hover
- **Behaviour:**
  - Drag to reorder vertically, through `TabReorder`'s Y axis.
  - Stacks become collapsible groups.
  - Arrangements and auto-close work as they do in the strip.
- **Width.**
  - New setting `verticalTabWidth`, with a default, a schema test and vectors.
  - The rail's edge drags to resize it, within `clampRailWidth`.
  - The host reads the width from the setting, so the page moves with it; no separate bridge call is needed.
  - Hidden with Ctrl+S, the rail gives the page its column and slides out from the left edge, as the app sidebar does.
- **Defaults.** `tabBarPosition` defaults to `left`. Switching position never loses tabs.
- **Tests (e2e):** switching positions keeps the tabs, the page bounds for each position are whole pixels, vertical drag reorders, and resizing moves the page.

### B. Favourites and pinned items (T6, T7), PR 2

- **Library.** A second fixed folder, Pinned (`PINNED_ID`), next to Favourites. Folders inside it are allowed. Items keep stable ids and `updatedAt` for later sync.
- **Saved tabs.**
  - A `TabRecord` gets `itemId`, which ties it to a library item.
  - `TabService` keeps at most one tab per item in each window.
  - Opening an item activates its tab, or creates one bound to it.
  - Closing a bound tab removes the tab record, and the item stays.
  - Commands: Reset to saved address, Update to this page, Unpin, Rename, Remove from Favourites.
- **Pin.** Pin and Unpin keep their shortcut. They now add a tab to Pinned, or remove it, and bind it. In the top strip, a bound tab still draws as a pinned chip at the front, so both positions agree.
- **Favourite tiles.**
  - A grid of icon tiles, as many per row as the width fits.
  - The active tile is highlighted.
  - A folder inside Favourites opens a menu, as the bar does now.
- **The Pinned section.**
  - Folders that collapse and expand.
  - Items show their icon and title.
  - An item whose tab is loaded looks different from one that is unloaded.
- **Drag between sections:**
  - A tab dropped on the tiles becomes a favourite.
  - A tab dropped on Pinned, or into one of its folders, becomes a pinned item.
  - An item dropped among today's tabs is unpinned.
  - Items reorder within their sections.
- **Icons.** Favicons stay in memory only (B22). An unloaded item shows a letter tile until its page has loaded once in this run.
- **Tests:** unit tests for binding (one tab per item, closing keeps the item, reset, update) and the library moves. e2e tests for pinning, a relaunch keeping the items but no pages, clicking a tile twice switching instead of opening a second tab, and the drag between sections.

### C. Split view (T5), PR 3

Unchanged from the earlier plan:
- Each pane's page is laid out with `paneRects`.
- The dividers and the drop zones on a page edge are new `OverlayHost` levels (W-6).
- The focused pane drives the address bar.
- Splits survive sleeping and session restore.
- Ordering treats a split as one unit.
- Ways to make one: "Split with new tab", "Add to split…", Ctrl+Shift+\\, and dragging a tab onto a page edge.

### D. Polish (T4), PR 4

- Collapse to icons at `VERTICAL_TABS.collapsedWidth`, expanding on hover.
- New setting `verticalTabsCollapsed`.
- The rail renders only the rows on screen, so 300 tabs stay smooth.
- Light and dark screenshots of the left and right rail, and of a 2-pane and a 4-pane split, in `docs/screenshots/`.

## Verification

- Unit tests next to the code, and e2e tests in `e2e/vertical-tabs.spec.ts`, `e2e/saved-tabs.spec.ts` and `e2e/split.spec.ts`.
- A real-input case for each PR: a real drag in the rail (PR 1), a real click on a favourite tile (PR 2), and a real divider drag (PR 3).
- `npm test`, `npm run lint`, `npm run typecheck` and `npm run test:e2e` all green, and a hand check of each PR in the real window before merging.
