# Phase 4: tab bar position + split view

Parity rows T1–T5 in `PARITY.md`. Builds on Phase 3 (PR #114). The visual design follows the existing tokens; the colour refresh is Phase 5.

## Foundation (done)

- `src/shared/layout/chrome.ts`: `chromeInsets()` gives the page's top, left and right insets for the `top`, `left` and `right` tab positions, with the Favourites bar and docked panels counted. Every inset is a multiple of 4. `VERTICAL_TABS` holds the rail's default, minimum, maximum and collapsed widths, and `clampRailWidth()` enforces them.
- `ZingWindow.chromeInsets()` / `setRailWidth()`: the page view, find bar, overview and floating panels all take their position from it. When tabs are on the left, the app sidebar folds into the rail and no longer takes a column of its own.
- `src/shared/layout/split.ts` (Phase 2): split layouts, whole-pixel pane rects, divider drag, add/remove panes, `stripUnits`, and `splitFromDrop`.

## Work

### A. Vertical tabs (T1, T2, T3, T4)

- **Chrome renderer:**
  - When `tabBarPosition` is `left` or `right`, the tab strip row is removed: the toolbar becomes the top band and carries the drag region and space for the caption buttons.
  - The rail is a column on its side, sized `railWidth` and reported to main with a new `chrome:rail-width` IPC call into `ZingWindow.setRailWidth`.
  - Switching position must not lose tabs.
- **The rail itself (T2):**
  - Pinned tabs form an icon grid at the top.
  - Stacks are collapsible groups instead of dropdowns.
  - Tabs drag-reorder vertically, using `tabReorder`'s generalised axis.
  - The width can be resized by dragging the rail edge (`clampRailWidth`).
  - Collapse-to-icons uses `VERTICAL_TABS.collapsedWidth`, expanding on hover over the page edge.
  - New settings `verticalTabWidth` and `verticalTabsCollapsed`, each with a default, a schema test and a Settings row.
- **T3:** with tabs on the left, the app sidebar's items (Browser, Toolbox, recents, Settings) sit at the top of the rail, so there's one rail rather than two.
- **T4:** the rail renders only visible rows, so 300 tabs stay smooth.

### B. Split view (T5)

- **Main:**
  - Each window keeps a list of `SplitLayout`s.
  - When the active tab is in a split, every pane's view is shown at `paneRects(pageRect, layout)` with draggable dividers. The dividers are drawn in the overlay layer.
  - The focused pane is the active tab, so it drives the address bar.
  - Splits survive sleeping (`visibleInSplit` is wired into the sleep policy) and session restore (saved with the session).
  - Ordering treats a split as one unit, like stacks.
- **Creating a split:**
  - the tab context menu: "Split with new tab" and "Add to split…"
  - a shortcut, Ctrl+Shift+\\ (backslash)
  - dragging a tab onto a page edge, shown as an overlay drop zone during a tab drag
- **Closing and removing:**
  - Closing a pane removes it from the split.
  - A split of one dissolves.
  - "Remove from split" is in the context menu.
- **Strip:** `stripUnits` renders a split as one joined chip in the horizontal strip. The vertical rail draws it as a grouped row; work A renders it there using B's chip component.

## Verification

- Unit tests next to the code.
- e2e tests in `e2e/vertical-tabs.spec.ts` and `e2e/split.spec.ts`:
  - switch positions while keeping the tabs
  - page bounds on whole pixels for every position
  - resize and collapse the rail
  - create, close and dissolve a split
  - the focused pane drives the address bar
  - a split survives sleep and relaunch
- Light and dark screenshots of left and right tabs, and of a 2-pane and a 4-pane split, saved in `docs/screenshots/`.
- `npm test`, `npm run lint`, `npm run typecheck` and `npm run test:e2e` all green.
