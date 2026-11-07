// Port of the sign-in field detection and fill scripts from Krackn's `KeychainAutofill`
// (Features/Browser/KeychainAutofill.swift). Krackn handed the field to Apple's Passwords
// picker; here the script reports it to the host with `chrome.webview.postMessage`, and the
// host shows Zing's own account picker and answers a choice with `fillScript`.
//
// The host injects `detectionScript` with `AddScriptToExecuteOnDocumentCreatedAsync`, which
// runs it in every frame; it installs itself in the top frame only and reaches same-origin
// frames itself. WebView2 has no isolated world, so the page shares these globals and can
// read, replace or forge anything here. That's why the host takes the origin from WebView2's
// own `Source` for the message, never from the message, and why no saved username or password
// is ever handed to the page before the user chooses one in Zing's picker.
import { usernameHint } from './autofillFields'
import { signInLimits } from './signInOutcome'

/** The key every message from the script is posted under: `{ zingCredentialField: message }`. Anything else isn't the script's. */
export const autofillBridgeName = 'zingCredentialField'

/** Page globals the scripts share. */
const target = '__zingAutofillTarget'
const fill = '__zingAutofillFill'
const installed = '__zingAutofillInstalled'

/** The page function `fillScript` calls; the host builds the same call (Defaults/autofill.json). */
export const autofillFillFunction = fill

/**
 * Reports which sign-in field has focus, and where it is.
 *
 * Krackn's first version listened for `focusin` and nothing else, and the key
 * failed to appear on exactly the pages that matter:
 *   - a field the page focuses itself on load (Google's sign-in does) — focus
 *     landed before the script ran, and nobody clicks a field that already has
 *     the caret, so no event ever came;
 *   - fields inside a component's shadow DOM, where the event reports the
 *     component, not the input;
 *   - sign-in boxes in a same-site frame, common on school and work portals;
 *   - clicking a field that already had focus.
 * So it *asks* what's focused — on load, on clicks, keys and focus changes,
 * when the window comes back, and on a light poll while the page has focus —
 * reaching through shadow roots and same-origin frames. A cross-origin frame
 * can't be read (`contentDocument` is null), which is the browser enforcing
 * that third-party frames are never filled. A frame whose document is readable
 * but whose origin differs (a `document.domain` relaxation) is skipped too.
 *
 * Keys are never reported: the page could forge them. The host reads the
 * picker's arrows, Enter and Esc from real key presses itself.
 *
 * It also reports sign-ins, for the save and update prompts (SignInWatch in the host):
 *   - `submit`: a form with a visible password box was submitted (or Enter was pressed in
 *     a form-less one, or its sign-in button clicked), with the username and password in
 *     it. A form with two different passwords (change password) reports nothing.
 *   - `landed`: whether the page now has a visible password box. Sent once for every new
 *     document, and, after a submit, for a page that swaps its form for something else
 *     without navigating (only when the submitted password field has left the document for five
 *     checks; a hidden, disabled or covered form is still the form), and again with
 *     password:true if a password box comes back afterwards, so the host can withdraw a prompt (watched for as long as a
 *     prompt can stay open).
 * The host decides what they mean, takes the origin from WebView2's own Source for the
 * message, and holds the credentials for 10 seconds at most.
 *
 * `kind` here is `fieldKind` in autofillFields.ts; keep the two in step.
 */
export const detectionScript = `(() => {
  // The top frame reaches same-origin frames itself; a frame's own copy does nothing.
  if (window.top !== window) return;
  // Installed once per document, however often the host injects it.
  if (window.${installed}) return;
  const webview = window.chrome && window.chrome.webview;
  if (!webview || typeof webview.postMessage !== 'function') throw new Error('Zing autofill: chrome.webview is missing');
  const post = webview.postMessage.bind(webview);
  const report = m => post({ ${JSON.stringify(autofillBridgeName)}: m });
  window.${installed} = true;

  const kind = el => {
    if (!el || el.tagName !== 'INPUT' || el.disabled || el.readOnly) return null;
    const type = (el.type || '').toLowerCase();
    const ac = (el.autocomplete || '').toLowerCase();
    if (type === 'password') return 'password';
    if (ac.includes('username') || ac === 'email' || type === 'email') return 'username';
    const hint = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('aria-label') || '')
                  + ' ' + (el.placeholder || '')).toLowerCase();
    if ((type === 'text' || type === '' || type === 'tel') && /${usernameHint.source}/.test(hint)) return 'username';
    return null;
  };
  // A frame's document, only when it is on this page's own origin.
  const sameOrigin = frame => {
    let doc = null;
    try { doc = frame.contentDocument; } catch (e) { return null; }
    if (!doc) return null;
    try { return doc.location && doc.location.origin === window.location.origin ? doc : null; } catch (e) { return null; }
  };
  // The focused element, through shadow roots and same-origin frames,
  // with the offset of any frames it's inside.
  const focused = () => {
    let el = document.activeElement, x = 0, y = 0;
    for (let depth = 0; depth < 6 && el; depth++) {
      if (el.shadowRoot && el.shadowRoot.activeElement) { el = el.shadowRoot.activeElement; continue; }
      if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
        const doc = sameOrigin(el);
        if (!doc) return { el: null, x, y };   // another site's frame: never
        const r = el.getBoundingClientRect();
        x += r.left + el.clientLeft; y += r.top + el.clientTop;
        el = doc.activeElement;
        continue;
      }
      break;
    }
    return { el, x, y };
  };
  let lastKey = '';
  const check = () => {
    const { el, x, y } = focused();
    const k = kind(el);
    if (!k) {
      if (lastKey) { lastKey = ''; window.${target} = null; report({ kind: 'none' }); }
      return;
    }
    const r = el.getBoundingClientRect();
    const m = { kind: k, x: x + r.left, y: y + r.top, w: r.width, h: r.height };
    window.${target} = el;
    const key = [m.kind, m.x, m.y, m.w, m.h].map(v => typeof v === 'number' ? Math.round(v) : v).join(',');
    if (key !== lastKey) { lastKey = key; report(m); }
  };
  const soon = () => setTimeout(check, 0);
  // --- Sign-in reports ---
  const visible = el => {
    const view = el.ownerDocument && el.ownerDocument.defaultView;
    if (!el.isConnected || !view) return false;
    const style = view.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };
  const passwordBoxes = scope => Array.prototype.filter.call(scope.querySelectorAll('input'),
    el => (el.type || '').toLowerCase() === 'password' && !el.disabled && visible(el));
  const documents = () => {
    const docs = [document];
    document.querySelectorAll('iframe, frame').forEach(f => { const d = sameOrigin(f); if (d) docs.push(d); });
    return docs;
  };
  const hasPasswordBox = () => documents().some(d => passwordBoxes(d).length > 0);
  // The username and password typed into a form (or document), or null when it isn't a plain sign-in.
  const credentials = scope => {
    const boxes = passwordBoxes(scope).filter(el => el.value);
    if (boxes.length === 0) return null;
    // Sign-up forms repeat the password; a change-password form has two different ones.
    if (boxes.some(el => el.value !== boxes[0].value)) return null;
    const password = boxes[0].value;
    if (password.length > ${signInLimits.passwordMax}) return null;
    const inputs = Array.prototype.slice.call(scope.querySelectorAll('input'));
    const before = inputs.slice(0, inputs.indexOf(boxes[0])).filter(el => kind(el) === 'username' && el.value && visible(el));
    const username = before.length ? before[before.length - 1].value : '';
    if (!username || username.length > ${signInLimits.usernameMax}) return null;
    return { username, password, boxes };
  };
  // After a submit, a page that stays put (a single-page app) may swap the form for something else. A page that
  // navigates posts its own 'landed' when it loads. Five checks in a row (about 2 s) without the submitted field is a landing: a spinner that unmounts the form is not one.
  let watching = 0;
  // Only the submitted password field leaving the document counts: a form that is hidden, disabled or covered by a spinner is
  // still the form. Once it has gone, the watch goes on for as long as a prompt can stay open, and a password box back on the page
  // is reported so the host can withdraw a prompt (the form was only unmounted while the sign-in was checked).
  const watchLanding = boxes => {
    const token = ++watching;
    const started = Date.now();
    let gone = 0, left = false;
    const tick = () => {
      if (token !== watching || Date.now() - started > (left ? ${signInLimits.watchMs} : ${signInLimits.holdMs})) return;
      if (!left) {
        // A form mounted again (a new element) restarts the count: it is the sign-in page back, not a landing.
        gone = boxes.every(b => !b.isConnected) && !hasPasswordBox() ? gone + 1 : 0;
        if (gone >= ${signInLimits.settleChecks}) { left = true; report({ kind: 'landed', password: false }); }
      } else if (hasPasswordBox()) {
        report({ kind: 'landed', password: true });
        return;
      }
      setTimeout(tick, 400);
    };
    setTimeout(tick, 400);
  };
  let lastSubmit = '', lastSubmitAt = 0, submittedHere = false;
  const submitted = scope => {
    const c = credentials(scope);
    if (!c) return;
    // A form fires several of these for one submit (the click, Enter, the submit event).
    const key = JSON.stringify([c.username, c.password]);
    const now = Date.now();
    if (key === lastSubmit && now - lastSubmitAt < 1000) return;
    lastSubmit = key; lastSubmitAt = now; submittedHere = true;
    report({ kind: 'submit', username: c.username, password: c.password });
    watchLanding(c.boxes);
  };
  const onSubmit = e => { if (e.target && e.target.tagName === 'FORM') submitted(e.target); };
  const onEnter = e => {
    const el = e.target;
    if (e.key === 'Enter' && el && el.tagName === 'INPUT' && (el.type || '').toLowerCase() === 'password') submitted(el.form || el.ownerDocument);
  };
  const signInLabel = /sign.?in|log.?in|continue|submit|next/i;
  const onClick = e => {
    const button = e.target && e.target.closest ? e.target.closest('button, input[type=submit], input[type=button], [role=button]') : null;
    if (!button) return;
    const label = button.textContent || button.value || button.getAttribute('aria-label') || '';
    if (signInLabel.test(label)) submitted(button.form || button.ownerDocument);
  };

  const events = ['focusin', 'focusout', 'pointerdown', 'keyup'];
  const listen = doc => {
    events.forEach(t => doc.addEventListener(t, soon, true));
    doc.addEventListener('submit', onSubmit, true);
    doc.addEventListener('keydown', onEnter, true);
    doc.addEventListener('click', onClick, true);
  };
  listen(document);
  // Focus and clicks inside a frame never reach this document, so listen
  // inside each same-origin frame too, as it loads. Cross-origin frames
  // throw or give null on access and are skipped — never watched, never filled.
  const hooked = new WeakSet();
  const hookFrames = () => {
    document.querySelectorAll('iframe, frame').forEach(f => {
      const doc = sameOrigin(f);
      if (!doc || hooked.has(doc)) return;
      hooked.add(doc);
      listen(doc);
    });
  };
  document.addEventListener('load', () => { hookFrames(); soon(); }, true);   // frames loading
  hookFrames();
  window.addEventListener('focus', soon, true);
  window.addEventListener('scroll', check, { passive: true, capture: true });
  window.addEventListener('resize', check, { passive: true });
  window.addEventListener('load', () => {
    hookFrames(); soon();
    // Whether this new document looks like a sign-in that worked; the host knows if one was waiting. A document
    // that was itself submitted before it finished loading is the page being left, not the one landed on.
    setTimeout(() => { if (!submittedHere) report({ kind: 'landed', password: hasPasswordBox() }); }, 300);
  });
  setInterval(() => { if (document.hasFocus()) check(); }, 700);

  // Puts a value into a field as if typed: the native value setter of the
  // field's *own* window (it may be inside a frame), so frameworks like React
  // see it, then input and change events. Never moves the focus, which would
  // report another field and open the picker again.
  const put = (el, value) => {
    const proto = (el.ownerDocument.defaultView || window).HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  // The other half of the credential: the first password box after a username
  // box, or the last username box before a password box, in its form (or document).
  const partner = (el, wanted) => {
    const scope = el.form || el.ownerDocument;
    const inputs = Array.prototype.slice.call(scope.querySelectorAll('input'));
    const at = inputs.indexOf(el);
    if (at < 0) return null;
    const candidates = wanted === 'password' ? inputs.slice(at + 1) : inputs.slice(0, at).reverse();
    return candidates.find(other => kind(other) === wanted) || null;
  };
  // Fills the field that was focused and its other half; only if the field is
  // still in the page and still a sign-in field.
  window.${fill} = (username, password) => {
    const el = window.${target};
    const k = el && el.isConnected ? kind(el) : null;
    if (!k) return false;
    const other = partner(el, k === 'username' ? 'password' : 'username');
    if (other) put(other, k === 'username' ? password : username);
    put(el, k === 'username' ? username : password);
    return true;
  };

  check();
})();`

/**
 * Run in the page after the user picks a login and passes Windows Hello: fills the
 * reported field and its other half, and evaluates to whether it did. The values
 * travel as JSON string literals, which are safe JavaScript literals for any text.
 * It fills only while the page is still on `origin`, the one the host checked
 * before Hello: a navigation during Hello must not receive the login.
 */
export function fillScript(origin: string, username: string, password: string): string {
	return `location.origin === ${JSON.stringify(origin)} && window.${fill}(${JSON.stringify(username)}, ${JSON.stringify(password)})`
}
