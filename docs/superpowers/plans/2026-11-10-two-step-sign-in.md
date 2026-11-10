# Two-Step Sign-In Save Prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Offer the vault's Save/Update prompt on sign-ins that take the username on one page and the password on the next (Google, Microsoft, banks).

**Architecture:** The page script reports a new `username-step` message when a username-only form is submitted, and reports a password-only submit with an empty username. `SignInWatch` remembers each tab's latest username step in memory for 5 minutes and fills it into an empty-username submit on the same site, following Chrome's `IsPossibleSingleUsernameAvailable`. Everything after that (success rule, prompt, never-save, update) is the existing path, keyed on the password page's origin.

**Tech Stack:** C# .NET 9 (`host/Zing.Core`, xUnit), TypeScript page script (`src/shared/vault/autofillScript.ts`, vitest with a fake DOM), Playwright e2e over CDP.

**Spec:** the design agreed in chat with the owner on 2026-11-10 (follow Chrome). Summarised here as the source of truth:

- Page: submitting a form with a filled username box and no visible password box (submit event, Enter in a username box, or a sign-in/next/continue button) posts `{kind: 'username-step', username}`. A password submit whose form has no filled visible username reports `username: ''` plus `hiddenUsername` when a hidden field marked `autocomplete~=username` or `type=email` is filled (Google's password page has one). Password boxes that look like one-time codes are never a password.
- Host: per tab, the latest username step only, in memory only. Forgotten after **5 minutes** (Chrome `kSingleUsernameTimeToLive = base::Minutes(5)`), after a successful sign-in in that tab, when the tab closes, when the vault turns off, and when the user navigates the tab themselves (address bar, bookmark/library item, Home, saved tab).
- Pairing: only into a submit whose own visible username is empty, and only on the same site: same scheme, same port, same registrable domain by the **full Public Suffix List, private section included** (owner's decision after review: Zing's short `RegistrableDomain` list would make `alice.github.io`/`bob.github.io` one site). `id.bank.com` → `auth.bank.com` pairs; `login.live.com` → `login.microsoftonline.com` and `a.github.io` → `b.github.io` do not. Order: a visible username on the password page; else the same-site step; else `hiddenUsername`; else nothing.
- Unchanged: the success check, the saved login's origin and "never for this site" all use the password page's origin. A password with no username anywhere gets no prompt (owner-approved call; Chrome would offer one).

## Global Constraints

- Tabs for indentation in C# and TS; match the surrounding style (comments only for non-obvious why).
- Credentials never logged, persisted, or sent to the renderer; the remembered username lives only in `SignInWatch`.
- Page messages never throw on parse (Ruling 29): bad shapes are `Dropped`. Username ≤ 512 UTF-16 units (`SignInWatch.UsernameMax`).
- The origin is always WebView2's frame source, never from the message (Ruling 33).
- e2e: never start Zing.exe without `ZING_E2E=1` and a scratch `ZING_USER_DATA` (the `launchZing` helper does this); run Playwright only through `C:/Users/USER/AppData/Local/Temp/z6/.superpowers/sdd/phase-6-plan/pw.sh`, in the background. Never run `test:e2e:real-input`. No clipboard, no real Windows Hello.
- Commits short and lowercase, ending with the `Co-Authored-By` line. Never push to main.

## Review Focus

0. (From review) A bank's code page after the password page uses `type=password`: the code must never pair with the step and be saved/updated as the password. Page-side OTP filter (Task 1) + host test (Task 3).
1. A newsletter/search box that looks like a username (`type=email`) submitted on a site, then a password-only sign-in there within 5 min → the newsletter email is offered. Accepted (Chrome does the same); the user can choose Not now. No test; noted in the PR.
2. A wrong password on the password page, then the right one → the second submit must still get the remembered username (it is kept until a *success*). Test in Task 2.
3. A username step on site A, then a password-only form on site B in the same tab → nothing offered. Test in Task 2.
4. A password page that has an empty visible username box (some banks render it) → treated as no username, so the remembered one is used. Test in Task 1.
5. Enter in a username box of a single-page form that already shows a password box → no username step (the existing "Enter in a text box does not submit" rule stays). Test in Task 1.

---

### Task 1: Page script reports username steps and password-only submits

**Files:**
- Modify: `src/shared/vault/autofillScript.ts` (doc comment at lines 61-72; `credentials` 169-181; `submitted` 207-217; `onEnter` 219-222)
- Test: `src/shared/vault/autofillScript.test.ts` (`describe('sign-in reports')`)

**Interfaces:**
- Produces: page message `{ kind: 'username-step', username: string }` (1 ≤ length ≤ 512); `{ kind: 'submit', username: string, password: string }` where `username` may now be `''`.

- [ ] **Step 1: Write the failing tests** (in `describe('sign-in reports')`; add a helper `usernameForm(username)` like `filledForm` but with only the email box)
  - `a username-only form reports a username step, and nothing else`: `submit(usernameForm('ada@example.test').form)` → `messages` equals `[{ kind: 'username-step', username: 'ada@example.test' }]`; no `landed` after `vi.advanceTimersByTime(5000)`.
  - `a username step is reported once per second however many events fire`: submit twice → 1 message; after 1100 ms submit again → 2.
  - `an empty or over-long username step reports nothing`: `usernameForm('')`, `usernameForm('u'.repeat(513))` → `[]`; `'u'.repeat(512)` → 1 message.
  - `a password-only form reports a submit with an empty username`: form with only a filled password box → `[{ kind: 'submit', username: '', password: 'hunter2' }]`.
  - `a password form sends a hidden username field separately`: `filledForm('', 'hunter2')` plus a third input `{ type: 'email', value: 'ada@example.test' }` made invisible via `getComputedStyle` (`display: 'none'`) → `{ kind: 'submit', username: '', password: 'hunter2', hiddenUsername: 'ada@example.test' }`. A hidden `{ type: 'text', name: 'user_id' }` is not used (no `hiddenUsername` key).
  - `a box that looks like a one-time code is not a password`: password-only forms whose box has `autocomplete: 'one-time-code'`, or name/id matching `otp|one.?time|code|pin|token|mfa|2fa|verif`, report nothing on submit.
  - Update existing `ignores a hidden password box and anything that is not a form`: with the password box hidden the form is a username step, so expect `[{ kind: 'username-step', username: 'ada' }]` (Google's first page has a hidden password box); rename accordingly.
  - Change the existing `reports nothing without a username or a password, or past the limits`: `filledForm('', 'hunter2')` now reports `{ kind: 'submit', username: '', password: 'hunter2' }`; the other three cases still report nothing. Rename it `reports nothing without a password, or past the limits; an empty username is sent empty`.
  - `Enter in a username box with no password box on the page reports a username step`: form-less email box with a value, `doc.frames = [user]`, press Enter on it → `[{ kind: 'username-step', username }]`.
  - The existing `Enter in a form-less password box reports a submit; Enter in a text box does not` must still pass unchanged (Review Focus 5).

- [ ] **Step 2: Run them to see them fail**

Run: `npx vitest run src/shared/vault/autofillScript.test.ts`
Expected: the new tests FAIL (no `username-step` messages, empty usernames dropped).

- [ ] **Step 3: Implement in `detectionScript`**
  - New `looksLikeCode(el)`: `autocomplete` contains `one-time-code`, or name/id/aria-label matches `/otp|one.?time|code|pin|token|mfa|2fa|verif/i`. `credentials` ignores such boxes (they still count for `hasPasswordBox`/landing).
  - `credentials(scope)`: username = last filled, visible username-kind input before the first password box, else `''`. When `''`, add `hiddenUsername` = last filled input in `scope` that is not visible and has `autocomplete` containing `username` or `type === 'email'` (omit the key when none). Keep the `usernameMax` check for both. Password rules otherwise unchanged.
  - New `usernameStep(scope)`: `null` when `passwordBoxes(scope)` is non-empty; else the last filled, visible username-kind input's value in `scope`, `null` if none or over `usernameMax`. Enter in a username box uses that box's own `el.value` instead (same checks).
  - `submitted(scope)`: if `credentials` gives nothing, try `usernameStep(scope)`; report `{ kind: 'username-step', username }` with the same 1-second dedupe key (use `JSON.stringify([username, null])` so it never collides with a submit). A username step does not set `submittedHere` and does not start `watchLanding`.
  - `onEnter`: Enter on a password box as today; Enter on an input whose `kind` is `'username'` calls only the username-step path for `el.form || el.ownerDocument`.
  - Update the doc comment's sign-in reports list with a `username-step` bullet.

- [ ] **Step 4: Regenerate the host's copy of the script and run the tests**

Run: `npm run gen:host-defaults` (rewrites `host/Zing.Core/Defaults/autofill.json`), then `npm test`
Expected: all PASS (`hostDefaults.test.ts` fails if the JSON drifts).

- [ ] **Step 5: Commit**

```bash
git add src/shared/vault/autofillScript.ts src/shared/vault/autofillScript.test.ts host/Zing.Core/Defaults/autofill.json
git commit -m "vault: page script reports username steps and password-only submits"
```

### Task 2: Public Suffix List and the same-site rule

**Files:**
- Create: `host/Zing.Core/Defaults/public_suffix_list.dat` (copied verbatim from the download already in the scratchpad, `VERSION: 2026-11-09_13-00-17_UTC`, MPL 2.0 header kept)
- Modify: `host/Zing.Core/Zing.Core.csproj:19` (add `<None Include="Defaults\public_suffix_list.dat" CopyToOutputDirectory="PreserveNewest" />`)
- Create: `host/Zing.Core/PublicSuffix.cs`
- Create: `host/Zing.Core.Tests/PublicSuffixTests.cs`, `host/Zing.Core.Tests/vectors/test_psl.txt` (the list's official `tests/test_psl.txt`, verbatim; copy to output like the other test vectors)

**Interfaces:**
- Produces: `public static class PublicSuffix { public static string? RegistrableDomain(string host); }` — `host` lower-cased, trailing dot trimmed, IDN converted to ASCII; returns null for a host that *is* a public suffix (or empty); IPv4/IPv6 literals and single-label hosts (`localhost`) return themselves. Rules from both ICANN and PRIVATE sections; wildcard (`*.ck`) and exception (`!www.ck`) rules; the implicit `*` rule for unknown TLDs. Rules converted to ASCII with `IdnMapping` at load; loaded once, lazily, from `AppContext.BaseDirectory/Defaults/public_suffix_list.dat`; a missing file throws (no fallback). `RegistrableDomain` (the short list) is not changed.

- [ ] **Step 1: Failing tests**: theory over every `checkPublicSuffix('x', 'y')` line of `test_psl.txt` (`null` literal → null; skip lines whose input starts with `.` or that are mixed-case-only checks if the spec says they're undefined — keep any skipped line listed in the test with the reason); plus facts: `alice.github.io` → `alice.github.io`, `github.io` → null, `127.0.0.1` → `127.0.0.1`, `localhost` → `localhost`.
- [ ] **Step 2: Run** `npm run test:host -- --filter "FullyQualifiedName~PublicSuffixTests"` → FAIL (missing type).
- [ ] **Step 3: Implement** `PublicSuffix.cs` (a `HashSet<string>` each for rules, wildcards, exceptions; walk labels from longest candidate down).
- [ ] **Step 4: Run** the filter again → PASS.
- [ ] **Step 5: Commit** `git commit -m "core: public suffix list for same-site checks"`

### Task 3: Host remembers the username step and pairs it on the same site

**Files:**
- Modify: `host/Zing.Core/Vault/Autofill.cs:69-73, 112-115` (new message record, parsing; empty username and optional `hiddenUsername` in `submit`)
- Modify: `host/Zing.Core/Vault/SignInWatch.cs` (`SignInOutcomes.SameSite`, remembered username, `UserNavigated`; widen the `Message` kind filter at line 123)
- Modify: `host/Zing.Core/TabService.cs:426` (raise `Navigating`)
- Modify: `host/Zing.Core/ChromeApi.cs:205` (wire `Tabs.Navigating += SignIn.UserNavigated`)
- Modify: `src/shared/vault/signInOutcome.vectors.json` (`limits.usernameStepMs: 300000`; new `sameSite` cases) and `src/shared/vault/signInOutcome.ts` `signInLimits` (add `usernameStepMs: 300_000`, else `signInOutcome.test.ts:19` fails)
- Test: `host/Zing.Core.Tests/Vault/SignInWatchTests.cs` (rewrite `A_sign_in_with_no_username_is_dropped_before_it_is_held` at line 349: an empty username now parses; nothing is *held* without a step or hidden username)

**Interfaces:**
- Consumes: Task 1's page messages; Task 2's `PublicSuffix.RegistrableDomain`.
- Produces:
  - `AutofillMessage.UsernameStep(string Username)` (1..512 chars; otherwise `Dropped`). `AutofillMessage.Submitted(string Username, string Password, string? HiddenUsername = null)`: `Username` may be `""`; `HiddenUsername` 1..512 when present, else `Dropped`.
  - `static bool SignInOutcomes.SameSite(string a, string b)`: both parse as absolute URIs, same scheme, same port, and `PublicSuffix.RegistrableDomain(IdnHost)` equal and non-null.
  - `SignInWatch.Submitted(string pageId, string origin, string username, string password, string? hiddenUsername = null)`: when `username` is empty, use the page's same-site step, else `hiddenUsername`, else hold nothing. `FilledByZing` is checked with the resolved username.
  - `SignInWatch.UsernameStepFor = TimeSpan.FromMinutes(5)`; `void SignInWatch.UsernameStep(string pageId, string origin, string username)`; `void SignInWatch.UserNavigated(string pageId)`.
  - `event Action<string>? TabService.Navigating`, raised at the start of `TabService.Navigate(id, url)` (every caller of it is a navigation the user asked for: address bar, library item, Home, saved tab).

- [ ] **Step 1: Write the failing tests** (C#, in `SignInWatchTests`; `ManualScheduler.Advance` for time)
  - `The_username_step_keep_time_matches_the_shared_limits`: `limits.usernameStepMs` == `SignInWatch.UsernameStepFor.TotalMilliseconds` (300000).
  - Theory `Same_site_follows_the_shared_vectors` over new `sameSite` vectors: `https://id.bank.com` / `https://auth.bank.com` → true; `https://login.live.com` / `https://login.microsoftonline.com` → false; `https://a.example.co.uk` / `https://b.example.co.uk` → true; `https://alice.github.io` / `https://bob.github.io` → false; `https://uct.ac.za` / `https://wits.ac.za` → false; `https://bank.com` / `http://bank.com` → false; `https://bank.com` / `https://bank.com:8443` → false; `http://127.0.0.1:3000` / `http://127.0.0.1:3000` → true; `http://127.0.0.1:3000` / `http://127.0.0.2:3000` → false.
  - `A_two_step_sign_in_prompts_save_with_the_remembered_username`: `UsernameStep(Page, "https://id.example.com", "ada@example.test")`, then `SignIn("", "s3cret", origin: "https://auth.example.com")` → `Shown` is `save`, `Origin == "https://auth.example.com"`, `Username == "ada@example.test"`.
  - `A_two_step_sign_in_with_a_saved_login_prompts_update`: saved `ada@example.test` at `Site`; username step + password-only sign-in at `Site` → `Kind == "update"`, `Replaces == "ada@example.test"`.
  - `The_username_on_the_password_page_wins`: step `ada`, then `SignIn("bob@example.test", ...)` → `Username == "bob@example.test"`.
  - `A_username_step_on_another_site_is_not_used`: step at `https://a.example`, password-only at `https://b.example` → no prompt, `HeldCount == 0`.
  - `A_username_step_is_forgotten_after_five_minutes`: step, `Advance(UsernameStepFor + 1 ms)`, password-only sign-in → no prompt; and a fresh step with `Advance(UsernameStepFor - 1 s)` still prompts.
  - `A_wrong_password_keeps_the_username_for_the_next_try` (Review Focus 2): step; `Submitted(Page, Site, "", "wrong")` + `Landed(..., hasVisiblePasswordBox: true)`; then `SignIn("", "s3cret")` → prompt with the username.
  - `A_successful_sign_in_forgets_the_username_step`: step, sign-in succeeds, answer Not now; a later password-only sign-in → no prompt.
  - `Navigating_the_tab_yourself_closing_it_or_turning_the_vault_off_forgets_the_step`: three cases (`UserNavigated(Page)`, `PageClosed(Page)`, `Clear()`), each followed by a password-only sign-in → no prompt.
  - `The_step_belongs_to_its_own_tab`: step on `tab-2`, password-only sign-in on `Page` → no prompt.
  - `A_password_with_no_username_anywhere_prompts_nothing`: `SignIn("", "s3cret")` with no step → no prompt, `HeldCount == 0`.
  - `A_new_step_replaces_the_old_one`: step `ada`, step `bob`, password-only → `Username == "bob"`.
  - `A_hidden_username_is_used_without_a_step_and_loses_to_one`: `Submitted(Page, Site, "", "s3cret", hiddenUsername: "ada")` + success → `Username == "ada"`; with a same-site step `bob` first → `"bob"`.
  - `A_code_after_the_password_is_never_offered` (Review Focus 0, host side): step; `Submitted("", "real")` + `Landed(hasVisiblePasswordBox: true)`; the page script drops a code-looking submit, so nothing more is submitted; landing success → no prompt (and nothing held).
  - `A_login_Zing_filled_then_submitted_on_a_two_step_page_prompts_nothing`: `Filled(Page, Site, "ada", "s3cret")`, a saved `ada`/`s3cret` login, step `ada`, `SignIn("", "s3cret")` → no prompt.
  - `Message_routes_a_username_step_by_the_frame_source`: `Message(Page, new Uri("https://id.example.com/x"), new AutofillMessage.UsernameStep("ada"))` then password-only at `https://auth.example.com` → prompt with `ada`; from `zing://start/` → nothing remembered.
  - Message parsing: extend `A_submit_report_is_held_to_the_limits` so `(0, 10)` is now accepted; `hiddenUsername` of 513 or a non-string → `Dropped`; add theory `A_username_step_report_is_held_to_the_limits` (`512` → `UsernameStep`, `513` and `0` → `Dropped`, missing/non-string `username` → `Dropped`).
  - `Message` routes `UsernameStep` through the frame source's origin and ignores non-http(s) sources (same as the existing `Submitted` source test).

- [ ] **Step 2: Run them to see them fail**

Run: `npm run test:host -- --filter "FullyQualifiedName~SignInWatchTests"`
Expected: build errors for the missing members, then FAIL.

- [ ] **Step 3: Implement**
  - `Autofill.cs`: `case "username-step"` → `UsernameStep` when `Text(body, "username", UsernameMax)` is non-empty; `case "submit"` accepts an empty username string (still `Dropped` when absent or not a string).
  - `SignInWatch`: a private sealed class `Step(string Origin, string Username, IDisposable Timer)` in `Dictionary<string, Step> _steps` (one per page; a new step disposes and replaces the old). `UsernameStep` returns without the vault on or when `store.IsNeverSave(origin)`. `Message` routes `UsernameStep`. In `Submitted`, when `username` is empty, take the page's step if `SameSite(step.Origin, origin)`, otherwise return before `Drop`/holding anything. Forget the page's step on a `Success` in `Landed`, in `PageClosed`, `UserNavigated`, `Clear`/`ForgetHeld` and `Dispose`.
  - `TabService.Navigate`: raise `Navigating?.Invoke(id)` first. `ChromeApi`: `Tabs.Navigating += SignIn.UserNavigated;` next to `Tabs.Closed += SignIn.PageClosed;`.
  - Update the `SignInWatch` class summary to mention the username step.

- [ ] **Step 4: Run the host suite**

Run: `npm run test:host`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add host/Zing.Core host/Zing.Core.Tests src/shared/vault/signInOutcome.vectors.json src/shared/vault/signInOutcome.ts
git commit -m "vault: pair a two-step sign-in's username with its password"
```

### Task 4: e2e two-step sign-ins, README, full checks

**Files:**
- Modify: `e2e/vault.spec.ts` (the `signInSite` server at lines 433-459; new tests after the single-page test at line 696)
- Modify: `README.md` only if it has a "two-step sign-ins aren't offered a save prompt" note (`rg -n -i "two-step|two step" README.md docs PARITY.md`)

**Interfaces:**
- Consumes: Tasks 1-3 end to end.

- [ ] **Step 1: Add routes to `signInSite`**
  - `GET /two-step` → a form `action="/two-step/password" method="post"` with only `<input id="user" name="username" autocomplete="username">` and `<button>Next</button>`.
  - `POST /two-step/password` and `GET /two-step/password?typed=1` → a form `action="/login" method="post"` with only `<input id="pass" name="password" type="password">` and `<button>Sign in</button>` (no username field at all; `/login` already redirects to `/dashboard` and shows the form again for `wrong`).
  - `GET /spa-two-step` → a single document: step 1 is the username form; its submit is `preventDefault`ed and swaps `#root` for a password-only form after 300 ms; that form's submit swaps `#root` for `<h1>Dashboard</h1>` after 300 ms.

- [ ] **Step 2: Write the e2e tests**
  - `a two-page sign-in prompts Save with the username from the first page`: open `/two-step`, fill `#user` = `USERNAME`, click Next, `waitForURL(`${signInBase}/two-step/password`)`, fill `#pass`, click Sign in → `savePrompt` matches `{ kind: 'save', origin: signInBase, username: USERNAME }`; Save → `logins` has one with `USERNAME`, and `revealPassword` gives `PASSWORD`.
  - `a two-page sign-in to a saved login prompts an update naming it`: `save(zing, signInBase, USERNAME, 'old password')`, same flow → `{ kind: 'update', replaces: USERNAME }`.
  - `a single-page two-step sign-in prompts Save once with the right username`: `countPrompts(zing)` first, `/spa-two-step`, both steps, Dashboard visible → `{ kind: 'save', username: USERNAME }`, `promptsOpened` 1.
  - `typing an address after the username step forgets it`: `/two-step`, Next, then `open(zing, `${signInBase}/two-step/password?typed=1`)` (address-bar navigation, a distinct URL so `open` waits for the new document), sign in with the password, `waitForURL(`${signInBase}/dashboard`)` → after `settle`, `promptsOpened` is 0. This test only means something next to the first one, which proves pairing works on the same pages.
  - A `twoStep(page, username, password)` helper does the two steps (the existing `signIn` fills both boxes on one page).

- [ ] **Step 3: Run the vault e2e file through the lock wrapper (background)**

Run: `bash C:/Users/USER/AppData/Local/Temp/z6/.superpowers/sdd/phase-6-plan/pw.sh e2e/vault.spec.ts` (read the wrapper first for its exact arguments; it builds or expects a build per its header)
Expected: every test in `vault.spec.ts` passes, old and new.

- [ ] **Step 4: Remove the README note if present; run `npm test`, `npm run test:host`, `npm run lint`, `npm run typecheck`**

Expected: all clean.

- [ ] **Step 5: Commit**

```bash
git add e2e/vault.spec.ts README.md
git commit -m "vault: e2e for two-step sign-ins"
```
