import AppKit
import WebKit

/// Fills web sign-in fields from the Mac's own Passwords (iCloud Keychain).
///
/// macOS offers this in every app — Edit › AutoFill › Passwords… opens Apple's
/// picker, unlocked with Touch ID — but only for native text views: WKWebView
/// doesn't take the command (measured: it doesn't respond to
/// `_handleInsertFromPasswordsCommand:`; NSTextView does). Safari gets it
/// through an entitlement other browsers can't have.
///
/// So this bridges: when a sign-in field on a page has focus, a key appears in
/// it. Clicking it lays a transparent native text view over the field, opens
/// Apple's picker on *that*, and moves what the picker inserts into the page's
/// field. The password is never stored or logged by Krackn; it passes through
/// once, from Apple's picker to the field you clicked.
///
/// Safety rules, all enforced here:
///   * Main frame only — never into an embedded frame (an ad, a third party).
///   * The page script runs in its own content world, so the site's scripts
///     can't see the target reference, read the message, or spoof it.
///   * The value goes to the element that was focused when you clicked the
///     key, and only if it's still in the page.
enum KeychainAutofill {
    static let handlerName = "kracknCredentialField"
    /// Isolated from the page's own scripts: separate globals, same DOM.
    static let world = WKContentWorld.world(name: "KracknAutofill")

    enum Kind: String { case username, password }

    struct Field: Equatable {
        let kind: Kind
        /// In the page's CSS pixels, relative to the viewport.
        let rect: CGRect
    }

    static var userScript: WKUserScript {
        WKUserScript(source: detectionScript, injectionTime: .atDocumentEnd,
                     forMainFrameOnly: true, in: world)
    }

    /// Reports which sign-in field has focus, and where it is.
    ///
    /// The first version listened for `focusin` and nothing else, and the key
    /// failed to appear on exactly the pages that matter:
    ///   * a field the page focuses itself on load (Google's sign-in does) —
    ///     focus landed before this script ran, and nobody clicks a field that
    ///     already has the caret, so no event ever came;
    ///   * fields inside a component's shadow DOM, where the event reports the
    ///     component, not the input;
    ///   * sign-in boxes in a same-site frame, common on school and work portals;
    ///   * clicking a field that already had focus.
    /// So it now *asks* what's focused — on load, on clicks, keys and focus
    /// changes, when the window comes back, and on a light poll while the
    /// page has focus — reaching through shadow roots and same-origin frames.
    /// A cross-origin frame can't be read (`contentDocument` is null), which
    /// is the browser enforcing that third-party frames are never filled.
    static let detectionScript = """
    (() => {
      const kind = el => {
        if (!el || el.tagName !== 'INPUT' || el.disabled || el.readOnly) return null;
        const type = (el.type || '').toLowerCase();
        const ac = (el.autocomplete || '').toLowerCase();
        if (type === 'password') return 'password';
        if (ac.includes('username') || ac === 'email' || type === 'email') return 'username';
        const hint = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('aria-label') || '')
                      + ' ' + (el.placeholder || '')).toLowerCase();
        if ((type === 'text' || type === '' || type === 'tel') &&
            /user|login|e-?mail|account|identifier|sign.?in/.test(hint)) return 'username';
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
            try { doc = el.contentDocument; } catch (e) {}
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
      const post = m => window.webkit.messageHandlers.kracknCredentialField.postMessage(m);
      let lastKey = '';
      const check = () => {
        const { el, x, y } = focused();
        const k = kind(el);
        if (!k) {
          if (lastKey) { lastKey = ''; window.__kracknTarget = null; post({ kind: 'none' }); }
          return;
        }
        const r = el.getBoundingClientRect();
        const m = { kind: k, x: x + r.left, y: y + r.top, w: r.width, h: r.height };
        window.__kracknTarget = el;
        const key = [m.kind, m.x, m.y, m.w, m.h].map(v => typeof v === 'number' ? Math.round(v) : v).join(',');
        if (key !== lastKey) { lastKey = key; post(m); }
      };
      const soon = () => setTimeout(check, 0);
      const events = ['focusin', 'focusout', 'pointerdown', 'keyup'];
      events.forEach(t => document.addEventListener(t, soon, true));
      // Focus and clicks inside a frame never reach this document, so listen
      // inside each same-origin frame too, as it loads. Cross-origin frames
      // throw on access and are skipped — never watched, never filled.
      const hooked = new WeakSet();
      const hookFrames = () => {
        document.querySelectorAll('iframe, frame').forEach(f => {
          let doc = null;
          try { doc = f.contentDocument; } catch (e) {}
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
      check();
    })();
    """

    /// Puts `value` into the field that was focused, as if typed: the native
    /// value setter of the field's *own* window (it may be inside a frame), so
    /// frameworks like React see it, then input and change events.
    static let fillScript = """
    const el = window.__kracknTarget;
    if (!el || !el.isConnected) return false;
    const proto = (el.ownerDocument.defaultView || window).HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    el.focus();
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
    """

    /// Parses a message from the detection script. Nil for "no field".
    static func field(from body: Any) -> Field? {
        guard let dict = body as? [String: Any],
              let raw = dict["kind"] as? String, let kind = Kind(rawValue: raw),
              let x = dict["x"] as? Double, let y = dict["y"] as? Double,
              let w = dict["w"] as? Double, let h = dict["h"] as? Double,
              w > 0, h > 0
        else { return nil }
        return Field(kind: kind, rect: CGRect(x: x, y: y, width: w, height: h))
    }
}

/// Receives the detection script's messages and hands them to the right tab.
final class CredentialFieldRouter: NSObject, WKScriptMessageHandler {
    var tabForWebView: (WKWebView) -> BrowserTab? = { _ in nil }

    func userContentController(_ controller: WKUserContentController,
                               didReceive message: WKScriptMessage) {
        // Main frame only — never act on a message from an embedded frame.
        guard message.frameInfo.isMainFrame, let web = message.webView else { return }
        let field = KeychainAutofill.field(from: message.body)
        MainActor.assumeIsolated {
            guard let tab = tabForWebView(web) else { return }
            // While the picker is up, focus has moved to the bridge on purpose;
            // that "blur" mustn't take the key away mid-fill.
            if field == nil, KeychainFillBridge.shared.isActive(for: tab) { return }
            if tab.credentialField != field { tab.credentialField = field }
        }
    }
}

/// Lays a native text view over the page's field, opens Apple's Passwords
/// picker on it, and moves what it inserts into the page.
@MainActor
final class KeychainFillBridge {
    static let shared = KeychainFillBridge()

    private final class BridgeTextView: NSTextView {
        var onResign: (() -> Void)?
        override func resignFirstResponder() -> Bool {
            let ok = super.resignFirstResponder()
            if ok { DispatchQueue.main.async { [weak self] in self?.onResign?() } }
            return ok
        }
    }

    private var view: BridgeTextView?
    private weak var tab: BrowserTab?
    private var observer: NSObjectProtocol?
    private var timeout: Task<Void, Never>?

    func isActive(for tab: BrowserTab) -> Bool { view != nil && self.tab === tab }

    /// - Parameter presentPicker: false only in tests, which insert the value
    ///   themselves instead of putting Apple's picker on screen.
    func begin(for tab: BrowserTab, presentPicker: Bool = true) {
        guard AppSettings.passwordAutofill,
              let field = tab.credentialField,
              let window = tab.webView.window else { return }
        finish()
        let web = tab.webView

        // CSS pixels → the web view's points.
        let zoom = web.pageZoom
        var frame = CGRect(x: field.rect.minX * zoom, y: field.rect.minY * zoom,
                           width: field.rect.width * zoom, height: field.rect.height * zoom)
        if !web.isFlipped { frame.origin.y = web.bounds.height - frame.maxY }

        let text = BridgeTextView(frame: frame)
        text.isRichText = false
        text.drawsBackground = false
        text.textColor = .clear
        text.insertionPointColor = .clear
        text.isAutomaticTextCompletionEnabled = false
        // Tells Apple's picker which half of the credential this field wants.
        text.contentType = field.kind == .password ? .password : .username
        web.addSubview(text)

        view = text
        self.tab = tab
        text.onResign = { [weak self] in self?.finish() }
        observer = NotificationCenter.default.addObserver(
            forName: NSText.didChangeNotification, object: text, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.deliver() }
        }
        // If the picker is dismissed without a choice, don't leave the bridge
        // sitting over the page.
        timeout = Task { [weak self] in
            try? await Task.sleep(for: .seconds(90))
            guard !Task.isCancelled else { return }
            self?.finish()
        }

        window.makeFirstResponder(text)
        if presentPicker {
            NSApp.sendAction(NSSelectorFromString("_handleInsertFromPasswordsCommand:"),
                             to: text, from: nil)
        }
    }

    /// Test hook: what Apple's picker does when you choose an account.
    func simulatePickerInsert(_ value: String) {
        view?.insertText(value, replacementRange: NSRange(location: 0, length: 0))
    }

    private func deliver() {
        guard let text = view, let tab, !text.string.isEmpty else { return }
        let value = text.string
        // Wipe the bridge's copy before anything else.
        text.string = ""
        tab.webView.callAsyncJavaScript(
            KeychainAutofill.fillScript, arguments: ["value": value],
            in: nil, in: KeychainAutofill.world) { _ in }
        finish()
    }

    private func finish() {
        timeout?.cancel()
        timeout = nil
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        guard let text = view else { return }
        view = nil
        text.onResign = nil
        text.string = ""
        let web = tab?.webView
        text.removeFromSuperview()
        if let web, web.window?.firstResponder === text || web.window?.firstResponder == nil {
            web.window?.makeFirstResponder(web)
        }
        tab = nil
    }
}
