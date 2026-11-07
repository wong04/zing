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
 * that third-party frames are never filled.
 *
 * In a sign-in field, the down arrow and Esc are reported too (`{ kind: 'key' }`):
 * the down arrow moves the keyboard into the picker (or opens it again), Esc closes it.
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
  // The focused element, through shadow roots and same-origin frames,
  // with the offset of any frames it's inside.
  const focused = () => {
    let el = document.activeElement, x = 0, y = 0;
    for (let depth = 0; depth < 6 && el; depth++) {
      if (el.shadowRoot && el.shadowRoot.activeElement) { el = el.shadowRoot.activeElement; continue; }
      if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
        let doc = null;
        try { doc = el.contentDocument; } catch (e) { doc = null; }
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
  const pickerKeys = { ArrowDown: 'down', Escape: 'escape' };
  const onKey = e => {
    const key = pickerKeys[e.key];
    if (key && kind(focused().el)) report({ kind: 'key', key });
  };
  const events = ['focusin', 'focusout', 'pointerdown', 'keyup'];
  const listen = doc => {
    events.forEach(t => doc.addEventListener(t, soon, true));
    doc.addEventListener('keydown', onKey, true);
  };
  listen(document);
  // Focus and clicks inside a frame never reach this document, so listen
  // inside each same-origin frame too, as it loads. Cross-origin frames
  // throw or give null on access and are skipped — never watched, never filled.
  const hooked = new WeakSet();
  const hookFrames = () => {
    document.querySelectorAll('iframe, frame').forEach(f => {
      let doc = null;
      try { doc = f.contentDocument; } catch (e) { doc = null; }
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
  window.addEventListener('load', () => { hookFrames(); soon(); });
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
 */
export function fillScript(username: string, password: string): string {
	return `window.${fill}(${JSON.stringify(username)}, ${JSON.stringify(password)})`
}
