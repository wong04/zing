// Port of the sign-in field detection and fill scripts from Krackn's `KeychainAutofill`
// (Features/Browser/KeychainAutofill.swift). Krackn handed the field to Apple's Passwords
// picker; here the script reports it to the host through a function the preload exposes
// into the same isolated world, and the host answers with `fillScript`.
//
// Inject `detectionScript` into the *main frame only*, in the isolated world
// `autofillWorldId` (`webContents.executeJavaScriptInIsolatedWorld`, on each load). It
// reaches same-origin frames itself. The isolated world shares the DOM but not globals,
// so the site's scripts can't see the target, read the messages or spoof them.
import { usernameHint } from './autofillFields'

/** Isolated world for the autofill scripts (Electron reserves 999; any other positive id works). */
export const autofillWorldId = 1100

/**
 * The function the preload exposes in `autofillWorldId`
 * (`contextBridge.exposeInIsolatedWorld(autofillWorldId, autofillBridgeName, report)`).
 * It receives each message `parseFieldMessage` reads.
 */
export const autofillBridgeName = 'zingCredentialField'

/** Isolated-world globals the scripts share. */
const target = '__zingAutofillTarget'
const fill = '__zingAutofillFill'
const installed = '__zingAutofillInstalled'

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
 * `kind` here is `fieldKind` in autofillFields.ts; keep the two in step.
 */
export const detectionScript = `(() => {
  // Installed once per document, however often the host injects it.
  if (window.${installed}) return;
  const report = window[${JSON.stringify(autofillBridgeName)}];
  if (typeof report !== 'function') throw new Error('Zing autofill: the ${autofillBridgeName} bridge is missing');
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
  const events = ['focusin', 'focusout', 'pointerdown', 'keyup'];
  events.forEach(t => document.addEventListener(t, soon, true));
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
      events.forEach(t => doc.addEventListener(t, soon, true));
    });
  };
  document.addEventListener('load', () => { hookFrames(); soon(); }, true);   // frames loading
  hookFrames();
  window.addEventListener('focus', soon, true);
  window.addEventListener('scroll', check, { passive: true, capture: true });
  window.addEventListener('resize', check, { passive: true });
  window.addEventListener('load', () => { hookFrames(); soon(); });
  setInterval(() => { if (document.hasFocus()) check(); }, 700);

  // Puts a value into the field that was focused, as if typed: the native
  // value setter of the field's *own* window (it may be inside a frame), so
  // frameworks like React see it, then input and change events. Only if the
  // field is still in the page and still wants this half of the credential.
  window.${fill} = (value, wanted) => {
    const el = window.${target};
    if (!el || !el.isConnected || kind(el) !== wanted) return false;
    const proto = (el.ownerDocument.defaultView || window).HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    el.focus();
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  };

  check();
})();`

/**
 * Run in `autofillWorldId` after the user picks a login: fills the reported
 * field and evaluates to whether it did. The value travels as a JSON string
 * literal, which is a safe JavaScript literal for any text.
 */
export function fillScript(value: string, kind: 'username' | 'password'): string {
	return `window.${fill}(${JSON.stringify(value)}, ${JSON.stringify(kind)})`
}
