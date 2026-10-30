#!/bin/bash
# Compiles and runs every test suite against the CURRENT sources.
#
# These are standalone programs rather than XCTest bundles because the app is a
# SwiftPM executable built with Command Line Tools, where there's no test runner
# to host them. Each suite compiles the real source files directly, so it can
# never pass against a stale build.
#
#   ./run-tests.sh          # everything except the slow stress suite
#   ./run-tests.sh --all    # including stress and render suites
#   ./run-tests.sh unit     # a single suite by name

set -uo pipefail
cd "$(dirname "$0")"

SRC="Sources/Krackn"
BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT

# Paths contain a space ("All in One"), so every file list must be an array —
# a plain string variable word-splits and silently compiles nothing, which
# looks exactly like a passing run.
# Preferences and the palette generator. Almost everything reaches one of
# these now — the resolver reads the chosen DoH provider, the drop router and
# the background remover read the output folder — so they sit at the bottom of
# every suite's file list.
FOUNDATION=(
    "$SRC/App/AppSettings.swift"
    "$SRC/App/Accent.swift"
)
CORE=(
    "${FOUNDATION[@]}"
    "$SRC/Core/ShellRunner.swift"
    "$SRC/Features/Unblock/DoHResolver.swift"
    "$SRC/Features/Unblock/RelayEngine.swift"
    "$SRC/Features/Unblock/PinnedHTTPClient.swift"
    "$SRC/Features/Unblock/BlockDiagnostics.swift"
)
WEB=("$SRC/Features/Unblock/RelayedWebContent.swift")

total_pass=0
total_fail=0
suites_run=0
suites_failed=()

run_suite() {
    local name="$1"; shift
    local entry="$1"; shift
    local binary="$BUILD/$name"

    printf '\033[1m==> %s\033[0m\n' "$name"
    if ! swiftc -O -o "$binary" "$entry" "$@" 2>"$BUILD/$name.compile"; then
        printf '    \033[31mCOMPILE FAILED\033[0m\n'
        grep "error:" "$BUILD/$name.compile" | head -5 | sed 's/^/    /'
        suites_failed+=("$name (compile)")
        return 1
    fi
    # A suite that didn't produce a binary must never be reported as passing.
    if [[ ! -x "$binary" ]]; then
        printf '    \033[31mNO BINARY PRODUCED\033[0m\n'
        suites_failed+=("$name (no binary)")
        return 1
    fi

    local output exit_code
    output="$("$binary" 2>&1)"
    exit_code=$?
    suites_run=$((suites_run + 1))

    echo "$output" | grep -E "FAIL|ok  |passed|STRESS|memory|error" | sed 's/^/    /' | tail -25

    local summary p f
    summary="$(echo "$output" | grep -oE '[0-9]+ passed, [0-9]+ failed' | tail -1)"
    if [[ -n "$summary" ]]; then
        p="$(echo "$summary" | grep -oE '^[0-9]+')"
        f="$(echo "$summary" | sed -E 's/.* ([0-9]+) failed/\1/')"
        total_pass=$((total_pass + p))
        total_fail=$((total_fail + f))
    fi

    if [[ $exit_code -ne 0 ]]; then
        printf '    \033[31mexit %d\033[0m\n' "$exit_code"
        suites_failed+=("$name")
        return 1
    fi
    printf '    \033[32mok\033[0m\n'
    return 0
}

want="${1:-default}"

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "unit" ]]; then
    run_suite unit Tests/unit/main.swift "${CORE[@]}" \
        "$SRC/Features/Utilities/UtilityTools.swift" \
        "$SRC/Features/Utilities/DevTools.swift" \
        "$SRC/Features/Convert/FormatCatalog.swift" \
        "$SRC/Features/Convert/ConversionEngine.swift" \
        "$SRC/Core/ToolInstaller.swift" \
        "$SRC/Features/Network/DoHProfile.swift" \
        "$SRC/Features/Download/DownloadErrors.swift" \
        "$SRC/Features/Browser/SessionStore.swift" \
        "$SRC/Features/Browser/UserAgent.swift" \
        "$SRC/Features/Browser/FaviconChoice.swift" \
        "$SRC/Features/Browser/DownloadPolicy.swift" \
        "$SRC/Features/Browser/AddressInput.swift" \
        "$SRC/Features/Browser/StartShortcuts.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "network" ]]; then
    run_suite network Tests/network/main.swift "${CORE[@]}" \
        "$SRC/Features/Convert/ConversionEngine.swift" \
        "$SRC/Features/Convert/FormatCatalog.swift" \
        "$SRC/Core/ToolInstaller.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "cancellation" ]]; then
    run_suite cancellation Tests/cancellation/main.swift "${CORE[@]}"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "drop" ]]; then
    run_suite drop Tests/drop/main.swift "$SRC/App/DropRouter.swift" \
        "${FOUNDATION[@]}" "$SRC/Features/Unblock/DoHResolver.swift" \
        "$SRC/Features/Unblock/RelayEngine.swift" \
        "$SRC/Features/Unblock/PinnedHTTPClient.swift" \
        "$SRC/Core/ShellRunner.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "calc" ]]; then
    run_suite calc Tests/calc/main.swift "$SRC/Features/Utilities/Expression.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "bgremove" ]]; then
    run_suite bgremove Tests/bgremove/main.swift \
        "$SRC/Features/Utilities/BackgroundRemover.swift" \
        "${FOUNDATION[@]}" "$SRC/Features/Unblock/DoHResolver.swift" \
        "$SRC/Features/Unblock/RelayEngine.swift" \
        "$SRC/Features/Unblock/PinnedHTTPClient.swift" \
        "$SRC/Core/ShellRunner.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "graph" ]]; then
    run_suite graph Tests/graph/main.swift \
        "$SRC/Features/Utilities/Expression.swift" \
        "$SRC/Features/Utilities/GraphEngine.swift"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "tabs" ]]; then
    run_suite tabs Tests/tabs/main.swift "$SRC/Features/Browser/TabReorder.swift" \
        "$SRC/Features/Browser/WebViewHost.swift"
fi

# Tab ordering and the auto-close policy. Both are pure over a snapshot struct
# precisely so they can be tested without standing up a WKWebView.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "taborg" ]]; then
    run_suite taborg Tests/taborg/main.swift \
        "$SRC/Features/Browser/TabOrganizer.swift" \
        "$SRC/Features/Security/ThreatModel.swift"
fi

# The security heuristics. The false-positive cases matter more than the
# detections: a checker that flags ordinary sites gets switched off.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "security" ]]; then
    run_suite security Tests/security/main.swift \
        "$SRC/Features/Security/ThreatModel.swift" \
        "$SRC/Features/Security/ThreatScanner.swift" \
        "${CORE[@]}"
fi

# Palette generation. Guards two things a screenshot can't: that "Yellow" is
# still byte-identical to the original design, and that no generated family
# drops below AA contrast.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "theme" ]]; then
    run_suite theme Tests/theme/main.swift "${CORE[@]}"
fi

# The logo's SVG-path parser and the artwork's geometry. Guards against a bad
# re-export or a parser regression drawing something subtly wrong — invisible
# in a diff, obvious in a bounding box.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "logo" ]]; then
    run_suite logo Tests/logo/main.swift "$SRC/App/KracknLogo.swift"
fi

# Floating-panel collision layout and the sidebar's recents. Pure geometry,
# so a regression shows up as numbers rather than as overlapping windows.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "panels" ]]; then
    run_suite panels Tests/panels/main.swift "$SRC/App/PanelLayout.swift"
fi

# Toolbox search: real queries and the tool each must find first.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "toolbox" ]]; then
    run_suite toolbox Tests/toolbox/main.swift "$SRC/App/ToolboxSearch.swift"
fi

# Bookmarks, Reading List and the tab-shortcut arithmetic.
if [[ "$want" == "default" || "$want" == "--all" || "$want" == "library" ]]; then
    run_suite library Tests/library/main.swift "$SRC/Features/Browser/BrowserLibrary.swift"
fi

# Needs the whole app (models pull in views), like the persistence suite.
if [[ "$want" == "--all" || "$want" == "download" ]]; then
    APP_SOURCES=()
    while IFS= read -r f; do APP_SOURCES+=("$f"); done \
        < <(find "$SRC" -name "*.swift" ! -name "KracknApp.swift")
    NOMAIN2="$BUILD/AppNoMain2.swift"
    sed 's/@main//' "$SRC/App/KracknApp.swift" > "$NOMAIN2"
    run_suite download Tests/download/main.swift "$NOMAIN2" "${APP_SOURCES[@]}"
fi

# Drives real WKWebViews, so it lives with the slower UI suites.
if [[ "$want" == "--all" || "$want" == "persist" ]]; then
    APP_SOURCES=()
    while IFS= read -r f; do APP_SOURCES+=("$f"); done \
        < <(find "$SRC" -name "*.swift" ! -name "KracknApp.swift")
    NOMAIN="$BUILD/AppNoMain.swift"
    sed 's/@main//' "$SRC/App/KracknApp.swift" > "$NOMAIN"
    run_suite persist Tests/persist/main.swift "$NOMAIN" "${APP_SOURCES[@]}"
fi

if [[ "$want" == "default" || "$want" == "--all" || "$want" == "encoding" ]]; then
    run_suite encoding Tests/encoding/main.swift "${CORE[@]}" "${WEB[@]}"
fi

# Slow: drives real page loads and deliberate mid-flight teardowns.
if [[ "$want" == "--all" || "$want" == "stress" ]]; then
    run_suite stress Tests/stress/main.swift "${CORE[@]}" "${WEB[@]}"
fi

if [[ "$want" == "--all" || "$want" == "render" ]]; then
    run_suite render Tests/render/main.swift "${CORE[@]}" "${WEB[@]}"
fi

echo
echo "──────────────────────────────────────────────"
printf 'suites run: %d   assertions: %d passed, %d failed\n' \
    "$suites_run" "$total_pass" "$total_fail"
if [[ ${#suites_failed[@]} -gt 0 ]]; then
    printf '\033[31mfailed suites: %s\033[0m\n' "${suites_failed[*]}"
    exit 1
fi
printf '\033[32mall suites passed\033[0m\n'
