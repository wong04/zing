using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Zing.Core;
using Zing.Core.Mcp;
using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// The AI tools' way into a tab: DevTools calls on the tab's own CoreWebView2 only. It reads the main frame; other frames,
/// cross-origin ones included, are left out, and a PDF's text can't be read yet. Every call has <see cref="CallLimit"/>; a tab with no page gets one, unshown.
/// Input goes to the page through DevTools' Input domain, never the host's key handling; navigation goes through TabService, so
/// ThreatGate and the blocker see it. A page the AI has acted on passes its top-level form POSTs through <see cref="FormPostBackstop"/>.
/// </summary>
public sealed class CdpPageDriver(Func<string, MainWindow?> windowOf, Func<MainWindow?> frontWindow, AiShares shares, ConfirmGate gate, FormPostBackstop backstop, IScheduler scheduler, TimeSpan showWait) : IPageDriver
{
	public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(15);

	/// <summary>How long a screenshot of a hidden tab waits for the owner to switch to it (spec: background tabs).</summary>
	public static readonly TimeSpan ShowWait = TimeSpan.FromMinutes(2);

	private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

	private readonly HashSet<TabRef> _waitingToBeShown = [];

	/// <summary>A screenshot started or stopped waiting for its hidden tab to be shown; the tab's badge (Task 10) follows it.</summary>
	public event Action<TabRef>? ShowWaitChanged;

	public bool IsWaitingToBeShown(TabRef tab) => _waitingToBeShown.Contains(tab);

	public async Task<JsonElement> AxTree(TabRef tab)
	{
		var page = await PageAsync(tab);
		using var tree = await CallAsync(tab, page, "Accessibility.getFullAXTree", new { });
		return tree.RootElement.Clone();
	}

	public async Task<string> MainText(TabRef tab)
	{
		var page = await PageAsync(tab);
		// The viewer's text never reaches the DevTools accessibility tree (its plugin builds one only for a screen reader), and
		// its scripting messages read only a selection they leave behind in the owner's view, which no message clears.
		if (await IsPdfAsync(tab, page)) throw new AiToolException(AiErrors.Unsupported, "Reading the text of a PDF isn't available yet.");
		using var tree = await CallAsync(tab, page, "Accessibility.getFullAXTree", new { });
		return AxText.From(tree.RootElement);
	}

	public async Task<byte[]> Screenshot(TabRef tab, long? node, IReadOnlySet<long> blackOut)
	{
		var page = await PageAsync(tab);
		if (!IsShown(tab, page)) await WaitToBeShownAsync(tab, page);
		using var metrics = await CallAsync(tab, page, "Page.getLayoutMetrics", new { });
		var layout = metrics.RootElement.GetProperty("cssLayoutViewport");
		var visual = metrics.RootElement.GetProperty("cssVisualViewport");
		// Clips are in document coordinates; box models in the viewport's.
		PixelBox? origin = node is { } n ? await BoxAsync(tab, page, n) ?? throw new AiToolException(AiErrors.StaleRef, "The element is no longer on the page; take a new snapshot.") : null;
		object args = origin is { } box
			? new { format = "png", clip = new { x = box.X + layout.GetProperty("pageX").GetDouble(), y = box.Y + layout.GetProperty("pageY").GetDouble(), width = box.Width, height = box.Height, scale = 1 } }
			: new { format = "png" };
		using var shot = await CallAsync(tab, page, "Page.captureScreenshot", args);
		var png = Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!);
		if (blackOut.Count == 0) return png;
		var boxes = new List<PixelBox>();
		foreach (var masked in blackOut)
			if (await BoxAsync(tab, page, masked) is { } at) boxes.Add(at);
		var width = origin?.Width ?? visual.GetProperty("clientWidth").GetDouble();
		return await PaintAsync(png, boxes, origin?.X ?? 0, origin?.Y ?? 0, width);
	}

	public Uri UrlOf(TabRef tab) => new(TabsOf(tab).Get(tab.TabId).Url);

	public async Task<long?> NodeAtCentre(TabRef tab, long node)
	{
		var page = await PageAsync(tab);
		var (x, y) = await CentreAsync(tab, page, node);
		return await HitAsync(tab, page, node, x, y);
	}

	/// <summary>
	/// The node the page hit-tests at <paramref name="x"/>, <paramref name="y"/>, counted as <paramref name="node"/> when it is
	/// inside it and <paramref name="node"/> is the nearest interactive element above it (a button's icon or label span).
	/// </summary>
	private async Task<long?> HitAsync(TabRef tab, WebView2Page page, long node, double x, double y)
	{
		long hit;
		using (var found = await CallAsync(tab, page, "DOM.getNodeForLocation", new { x = (int)Math.Round(x), y = (int)Math.Round(y), includeUserAgentShadowDOM = false, ignorePointerEventsNone = true }))
		{
			if (!found.RootElement.TryGetProperty("backendNodeId", out var id)) return null;
			hit = id.GetInt64();
		}
		return hit == node || await OwnsAsync(tab, page, node, hit) ? node : hit;
	}

	public async Task Click(TabRef tab, long node)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		var (x, y) = await CentreAsync(tab, page, node);
		// Checked again here, after every other await and at the very point clicked: AiTools' check ran before the page could move.
		if (await HitAsync(tab, page, node, x, y) != node) throw new AiToolException(AiErrors.Covered, "Something else is on top of the element.");
		await MouseAsync(tab, page, new { type = "mouseMoved", x, y });
		await MouseAsync(tab, page, new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 });
		await MouseAsync(tab, page, new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 });
	}

	public async Task Type(TabRef tab, long node, string text, bool submit)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		await FocusAsync(tab, page, node);
		// Real text input, as an IME commits it: a framework's controlled field takes it where a script's value set wouldn't stick.
		using (await CallAsync(tab, page, "Input.insertText", new { text })) { }
		if (submit) await KeyAsync(tab, page, CdpKeys.Of("Enter"));
	}

	public async Task Select(TabRef tab, long node, string value)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		var picked = await CallOnAsync(tab, page, node, SelectOption, value);
		if (picked == "not a select") throw new ToolArgumentException("select", "ref", "is not a select element");
		if (picked == "no option") throw new ToolArgumentException("select", "value", "matches no option's value or label");
	}

	public async Task Scroll(TabRef tab, long? node, int deltaX, int deltaY)
	{
		var page = await PageAsync(tab);
		var (x, y) = node is { } n ? await CentreAsync(tab, page, n) : await ViewportCentreAsync(tab, page);
		await MouseAsync(tab, page, new { type = "mouseWheel", x, y, deltaX, deltaY });
	}

	public async Task PressKey(TabRef tab, long? node, string key)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		if (node is { } n) await FocusAsync(tab, page, n);
		await KeyAsync(tab, page, CdpKeys.Of(key));
	}

	public Task Navigate(TabRef tab, Uri url)
	{
		TabsOf(tab).Navigate(tab.TabId, url.AbsoluteUri);
		return Task.CompletedTask;
	}

	public async Task Back(TabRef tab)
	{
		await PageAsync(tab);
		var tabs = TabsOf(tab);
		if (!tabs.CanGoBack(tab.TabId)) throw new AiToolException(AiErrors.Unsupported, "The tab has nothing to go back to.");
		tabs.GoBack(tab.TabId);
	}

	public async Task Forward(TabRef tab)
	{
		await PageAsync(tab);
		var tabs = TabsOf(tab);
		if (!tabs.CanGoForward(tab.TabId)) throw new AiToolException(AiErrors.Unsupported, "The tab has nothing to go forward to.");
		tabs.GoForward(tab.TabId);
	}

	/// <summary>A tab behind the owner's, in the window they used last; its page starts unshown, so the owner's tab stays in front.</summary>
	public Task<TabRef> OpenTab(Uri url)
	{
		var window = frontWindow() ?? throw new AiToolException(AiErrors.TabGone, "No Zing window is open to put the tab in.");
		var tabs = window.Api?.Tabs ?? throw new AiToolException(AiErrors.TabGone, "The Zing window isn't ready yet.");
		var record = tabs.Create(url.AbsoluteUri);
		tabs.PageFor(record.Id);
		return Task.FromResult(new TabRef(window.Id, record.Id));
	}

	public Task CloseTab(TabRef tab)
	{
		TabsOf(tab).Close(tab.TabId);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Goes to <paramref name="url"/> in the tab's main frame, with the tab's own cookies, and turns the document response into a
	/// download (Content-Disposition: attachment, in DevTools' Fetch domain), so a page the engine could show, a PDF say, is
	/// saved instead. DownloadHost then saves it into Downloads under the server's name and lists it; the tab stays where it was.
	/// </summary>
	public async Task Download(TabRef tab, Uri url)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		if (_forcing.ContainsKey(page)) throw new AiToolException(AiErrors.Unsupported, "A download is already starting in this tab.");
		var started = new TaskCompletionSource();
		_forcing[page] = started;
		try
		{
			using (await CallAsync(tab, page, "Fetch.enable", new { patterns = new[] { RequestPattern, ResponsePattern } })) { }
			using (await CallAsync(tab, page, "Page.navigate", new { url = url.AbsoluteUri })) { }
			await LimitAsync(tab, page, started.Task, "download");
		}
		finally
		{
			_forcing.Remove(page);
			if (_holding.ContainsKey(page)) await ExpectingGoneAsync(tab, page, "Fetch.enable", new { patterns = new[] { RequestPattern } });
		}
	}

	private const string FillGroup = "zing-ai-fill";

	/// <summary>fill_login: the vault's AI fill (Autofill.FillForAi) on the page's sign-in fields, found and filled through DevTools.</summary>
	public async Task<(FillResult Result, IReadOnlySet<long> FilledNodes)> FillLogin(TabRef tab)
	{
		var page = await PageAsync(tab);
		var autofill = windowOf(tab.WindowId)?.Api?.Autofill ?? throw new TabGoneException(tab);
		string? fields = null;
		var nodes = new HashSet<long>();
		try
		{
			return await autofill.FillForAi(tab.TabId, page, async () =>
			{
				using (var found = await CallAsync(tab, page, "Runtime.evaluate", new { expression = AutofillScripts.LoginFields, objectGroup = FillGroup }))
				{
					if (!found.RootElement.GetProperty("result").TryGetProperty("objectId", out var array)) throw new AiToolException(AiErrors.Unsupported, "There is no sign-in form on the page.");
					fields = array.GetString()!;
				}
				var username = await ElementAsync(tab, page, fields, 0);
				var password = await ElementAsync(tab, page, fields, 1);
				if (username is { } u) nodes.Add(u);
				if (password is { } p) nodes.Add(p);
				var shown = (password ?? username)!.Value;
				using (await CallAsync(tab, page, "DOM.scrollIntoViewIfNeeded", new { backendNodeId = shown })) { }
				if (await BoxAsync(tab, page, shown) is not { } box) return null;
				return page.FieldInWindow(new CredentialField(password is null ? CredentialFieldKind.Username : CredentialFieldKind.Password, new Rect(box.X, box.Y, box.Width, box.Height), default));
			}, async (origin, username, password) =>
			{
				EnsureCurrent(tab, page);
				using var done = await CallAsync(tab, page, "Runtime.callFunctionOn", new { objectId = fields, functionDeclaration = AutofillScripts.PutLogin, arguments = new[] { new { value = origin }, new { value = username }, new { value = password } }, returnByValue = true });
				return done.RootElement.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True ? nodes : new HashSet<long>();
			});
		}
		finally
		{
			await ExpectingGoneAsync(tab, page, "Runtime.releaseObjectGroup", new { objectGroup = FillGroup });
		}
	}

	/// <summary>The backend node of item <paramref name="index"/> of the page array <paramref name="array"/>; null when it holds none.</summary>
	private async Task<long?> ElementAsync(TabRef tab, WebView2Page page, string array, int index)
	{
		string element;
		using (var item = await CallAsync(tab, page, "Runtime.callFunctionOn", new { objectId = array, functionDeclaration = "function (i) { return this[i] }", arguments = new[] { new { value = index } }, objectGroup = FillGroup }))
		{
			if (!item.RootElement.GetProperty("result").TryGetProperty("objectId", out var id)) return null;
			element = id.GetString()!;
		}
		using var node = await CallAsync(tab, page, "DOM.describeNode", new { objectId = element });
		return node.RootElement.GetProperty("node").GetProperty("backendNodeId").GetInt64();
	}

	private TabService TabsOf(TabRef tab) =>
		windowOf(tab.WindowId)?.Api?.Tabs is { } tabs && tabs.Has(tab.TabId) ? tabs : throw new TabGoneException(tab);

	/// <summary>The tab's page, started (unshown) if it had none and loaded; a page on a JavaScript dialog can't answer.</summary>
	private async Task<WebView2Page> PageAsync(TabRef tab)
	{
		var tabs = TabsOf(tab);
		var fresh = !tabs.HasPage(tab.TabId);
		var page = (WebView2Page)tabs.PageFor(tab.TabId);
		await LimitAsync(tab, page, page.Ready, "page start");
		EnsureCurrent(tab, page);
		if (fresh) await LoadedAsync(tab, page);
		if (page.HasOpenDialog) throw new DialogOpenException(tab);
		return page;
	}

	private async Task LoadedAsync(TabRef tab, WebView2Page page)
	{
		var clock = Stopwatch.StartNew();
		while (page.IsLoading)
		{
			if (page.HasOpenDialog) throw new DialogOpenException(tab);
			if (clock.Elapsed >= CallLimit) throw new CdpTimeoutException("page load");
			await DelayAsync(PollEvery);
			EnsureCurrent(tab, page);
		}
	}

	/// <summary>The tab went (closed, or its page slept or was replaced) while the driver waited: TabGone.</summary>
	private void EnsureCurrent(TabRef tab, WebView2Page page)
	{
		if (!page.IsOpen || TabsOf(tab).PageOf(tab.TabId) != page) throw new TabGoneException(tab);
	}

	private async Task<JsonDocument> CallAsync(TabRef tab, WebView2Page page, string method, object args)
	{
		var answer = await LimitAsync(tab, page, page.DevToolsAsync(method, JsonSerializer.Serialize(args)), method);
		return JsonDocument.Parse(answer);
	}

	/// <summary>
	/// <see cref="CallLimit"/> on one piece of DevTools work. A dialog that opens meanwhile stops the page answering until the
	/// owner closes it, so the call ends DialogOpen at once rather than at the limit.
	/// </summary>
	private async Task LimitAsync(TabRef tab, WebView2Page page, Task work, string what)
	{
		var clock = Stopwatch.StartNew();
		while (!work.IsCompleted)
		{
			if (!page.IsOpen) throw Abandon(work, new TabGoneException(tab));
			if (page.HasOpenDialog) throw Abandon(work, new DialogOpenException(tab));
			if (clock.Elapsed >= CallLimit) throw Abandon(work, new CdpTimeoutException(what));
			await Task.WhenAny(work, DelayAsync(PollEvery));
		}
		try
		{
			await work;
		}
		catch (ViewGoneException)
		{
			throw new TabGoneException(tab);
		}
	}

	private async Task<T> LimitAsync<T>(TabRef tab, WebView2Page page, Task<T> work, string what)
	{
		await LimitAsync(tab, page, (Task)work, what);
		return await work;
	}

	/// <summary>Work given up on may still fail later; its failure is seen here, not left unobserved.</summary>
	private static Exception Abandon(Task work, Exception reason)
	{
		work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		return reason;
	}

	private Task DelayAsync(TimeSpan delay)
	{
		// Not RunContinuationsAsynchronously: the wait resumes on the UI thread's synchronization context.
		var done = new TaskCompletionSource();
		scheduler.Schedule(delay, done.SetResult);
		return done.Task;
	}

	private bool IsShown(TabRef tab, WebView2Page page) => TabsOf(tab).IsShown(tab.TabId) && page.Visible && windowOf(tab.WindowId)?.IsMinimised == false;

	/// <summary>Never brings the tab forward: the owner does, within <c>showWait</c>, or the screenshot is NotVisible.</summary>
	private async Task WaitToBeShownAsync(TabRef tab, WebView2Page page)
	{
		_waitingToBeShown.Add(tab);
		ShowWaitChanged?.Invoke(tab);
		try
		{
			var clock = Stopwatch.StartNew();
			while (true)
			{
				await DelayAsync(PollEvery);
				EnsureCurrent(tab, page);
				if (!shares.IsShared(tab)) throw new AiToolException(AiErrors.NotShared, "The tab was unshared.");
				if (IsShown(tab, page)) return;
				if (clock.Elapsed >= showWait) throw new NotVisibleException(tab);
			}
		}
		finally
		{
			_waitingToBeShown.Remove(tab);
			ShowWaitChanged?.Invoke(tab);
		}
	}

	private const string SelectOption = """
		function (wanted) {
			if (!(this instanceof HTMLSelectElement)) return 'not a select'
			const options = [...this.options]
			const option = options.find((o) => o.value === wanted) ?? options.find((o) => o.label.trim() === wanted.trim())
			if (!option) return 'no option'
			this.value = option.value
			this.dispatchEvent(new Event('input', { bubbles: true }))
			this.dispatchEvent(new Event('change', { bubbles: true }))
			return 'picked'
		}
		""";

	/// <summary>
	/// Whether this element is interactive and the nearest interactive element at or above <c>hit</c>. A container ref (a card,
	/// a section) never owns what is over it, so a transparent overlay inside one still counts as covering.
	/// </summary>
	private const string Owns = """
		function (hit) {
			const interactive = 'a[href], button, input, select, textarea, summary, [contenteditable=""], [contenteditable="true"], [role=button], [role=link], [role=checkbox], [role=radio], [role=menuitem], [role=menuitemcheckbox], [role=menuitemradio], [role=tab], [role=option], [role=switch], [role=textbox], [role=combobox], [role=searchbox], [role=slider], [role=spinbutton]'
			if (!this.matches(interactive)) return false
			const element = hit.nodeType === Node.ELEMENT_NODE ? hit : hit.parentElement
			return element !== null && element.closest(interactive) === this
		}
		""";

	/// <summary>The centre of the node's content box in viewport CSS pixels, once scrolled into view; StaleRef when the page no longer has it.</summary>
	private async Task<(double X, double Y)> CentreAsync(TabRef tab, WebView2Page page, long backendNodeId)
	{
		JsonDocument model;
		try
		{
			using (await CallAsync(tab, page, "DOM.scrollIntoViewIfNeeded", new { backendNodeId })) { }
			model = await CallAsync(tab, page, "DOM.getBoxModel", new { backendNodeId });
		}
		catch (Exception error) when (error is not (TabGoneException or DialogOpenException or CdpTimeoutException))
		{
			// DevTools answers an error for a node that went or isn't rendered.
			throw new AiToolException(AiErrors.StaleRef, "The element is no longer on the page, or isn't shown; take a new snapshot.");
		}
		using (model)
		{
			var quad = model.RootElement.GetProperty("model").GetProperty("content").EnumerateArray().Select(v => v.GetDouble()).ToArray();
			return ((quad[0] + quad[2] + quad[4] + quad[6]) / 4, (quad[1] + quad[3] + quad[5] + quad[7]) / 4);
		}
	}

	private async Task<(double X, double Y)> ViewportCentreAsync(TabRef tab, WebView2Page page)
	{
		using var metrics = await CallAsync(tab, page, "Page.getLayoutMetrics", new { });
		var visual = metrics.RootElement.GetProperty("cssVisualViewport");
		return (visual.GetProperty("clientWidth").GetDouble() / 2, visual.GetProperty("clientHeight").GetDouble() / 2);
	}

	private async Task FocusAsync(TabRef tab, WebView2Page page, long backendNodeId)
	{
		await CentreAsync(tab, page, backendNodeId);
		using (await CallAsync(tab, page, "DOM.focus", new { backendNodeId })) { }
	}

	private async Task MouseAsync(TabRef tab, WebView2Page page, object args)
	{
		using (await CallAsync(tab, page, "Input.dispatchMouseEvent", args)) { }
	}

	private async Task KeyAsync(TabRef tab, WebView2Page page, CdpKey key)
	{
		object down = key.Text is { } text
			? new { type = "keyDown", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, text, unmodifiedText = text }
			: new { type = "rawKeyDown", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey };
		using (await CallAsync(tab, page, "Input.dispatchKeyEvent", down)) { }
		using (await CallAsync(tab, page, "Input.dispatchKeyEvent", new { type = "keyUp", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey })) { }
	}

	private async Task<string> ObjectOfAsync(TabRef tab, WebView2Page page, long backendNodeId)
	{
		try
		{
			using var resolved = await CallAsync(tab, page, "DOM.resolveNode", new { backendNodeId });
			return resolved.RootElement.GetProperty("object").GetProperty("objectId").GetString()!;
		}
		catch (Exception error) when (error is not (TabGoneException or DialogOpenException or CdpTimeoutException))
		{
			throw new AiToolException(AiErrors.StaleRef, "The element is no longer on the page; take a new snapshot.");
		}
	}

	/// <summary>Runs one of the driver's own functions on the node with <paramref name="argument"/>; never script an AI client wrote.</summary>
	private async Task<string?> CallOnAsync(TabRef tab, WebView2Page page, long backendNodeId, string function, string argument)
	{
		var objectId = await ObjectOfAsync(tab, page, backendNodeId);
		using var answer = await CallAsync(tab, page, "Runtime.callFunctionOn", new { objectId, functionDeclaration = function, arguments = new[] { new { value = argument } }, returnByValue = true });
		return answer.RootElement.GetProperty("result").TryGetProperty("value", out var value) ? value.GetString() : null;
	}

	private async Task<bool> OwnsAsync(TabRef tab, WebView2Page page, long target, long hit)
	{
		var targetId = await ObjectOfAsync(tab, page, target);
		var hitId = await ObjectOfAsync(tab, page, hit);
		using var answer = await CallAsync(tab, page, "Runtime.callFunctionOn", new { objectId = targetId, functionDeclaration = Owns, arguments = new[] { new { objectId = hitId } }, returnByValue = true });
		return answer.RootElement.GetProperty("result").GetProperty("value").GetBoolean();
	}

	/// <summary>Pages whose document requests pass through <see cref="FormPostBackstop"/>, and their tabs; a page is added before the AI first acts on it.</summary>
	private readonly Dictionary<WebView2Page, TabRef> _holding = [];

	/// <summary>Pages whose next main-frame document response becomes a download (<see cref="Download"/>), and what waits for it.</summary>
	private readonly Dictionary<WebView2Page, TaskCompletionSource> _forcing = [];

	private static readonly object RequestPattern = new { urlPattern = "*", resourceType = "Document", requestStage = "Request" };
	private static readonly object ResponsePattern = new { urlPattern = "*", resourceType = "Document", requestStage = "Response" };

	/// <summary>Pages already listening for Fetch.requestPaused, so a tab shared again doesn't listen twice.</summary>
	private readonly HashSet<WebView2Page> _listening = [];

	private const int InvalidArgument = unchecked((int)0x80070057);

	/// <summary>An unshared tab's page stops pausing its requests; one still held is answered no by the gate, which the unshare resolves.</summary>
	public void Unshared(TabRef tab)
	{
		foreach (var page in _holding.Where(p => p.Value == tab).Select(p => p.Key).ToList())
		{
			_holding.Remove(page);
			if (_forcing.Remove(page, out var forcing)) forcing.TrySetException(new AiToolException(AiErrors.NotShared, "The tab was unshared."));
			var asking = _asking.Remove(page, out var ids) ? ids.ToList() : [];
			if (page.IsOpen) TestHooks.Background(StopHoldingAsync(tab, page, asking), "ai form post watch");
		}
	}

	/// <summary>Requests paused for the owner's answer, by page; a request leaves when it is answered or failed.</summary>
	private readonly Dictionary<WebView2Page, HashSet<string>> _asking = [];

	/// <summary>
	/// Fails every request still waiting for the owner before turning Fetch off: Chromium lets paused requests go when Fetch is
	/// disabled, so a POST the unshare denied would otherwise be sent.
	/// </summary>
	private async Task StopHoldingAsync(TabRef tab, WebView2Page page, IReadOnlyList<string> asking)
	{
		foreach (var requestId in asking) await ExpectingGoneAsync(tab, page, "Fetch.failRequest", new { requestId, errorReason = "Aborted" });
		await ExpectingGoneAsync(tab, page, "Fetch.disable", new { });
	}

	/// <summary>
	/// Pauses the page's document requests in DevTools' Fetch domain, so the backstop can hold a top-level form POST until the
	/// owner answers: NavigationStarting can't wait for an answer, and doesn't say the method. Every other request goes on at once.
	/// </summary>
	private async Task HoldFormPostsAsync(TabRef tab, WebView2Page page)
	{
		foreach (var closed in _holding.Keys.Where(p => !p.IsOpen).ToList()) _holding.Remove(closed);
		_listening.RemoveWhere(p => !p.IsOpen);
		if (_holding.ContainsKey(page)) return;
		string mainFrame;
		using (var tree = await CallAsync(tab, page, "Page.getFrameTree", new { }))
			mainFrame = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
		EnsureCurrent(tab, page);
		if (!_holding.TryAdd(page, tab)) return;
		if (_listening.Add(page)) page.OnDevToolsEvent("Fetch.requestPaused", json => Paused(tab, page, mainFrame, json));
		using (await CallAsync(tab, page, "Fetch.enable", new { patterns = new[] { RequestPattern } })) { }
	}

	private void Paused(TabRef tab, WebView2Page page, string mainFrame, string json)
	{
		using var paused = JsonDocument.Parse(json);
		var root = paused.RootElement;
		var requestId = root.GetProperty("requestId").GetString()!;
		if (root.TryGetProperty("responseStatusCode", out _) || root.TryGetProperty("responseErrorReason", out _))
		{
			Responded(tab, page, mainFrame, requestId, root);
			return;
		}
		var request = root.GetProperty("request");
		// A closed tab is unshared first, so only a live tab's address is asked for.
		var site = shares.IsShared(tab) ? UrlOf(tab) : null;
		var held = site is not null && backstop.Holds(tab, site, request.GetProperty("method").GetString()!, root.GetProperty("frameId").GetString() == mainFrame);
		var to = Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var target) ? target.IdnHost : "another page";
		if (held)
		{
			if (!_asking.TryGetValue(page, out var ids)) _asking[page] = ids = [];
			ids.Add(requestId);
		}
		TestHooks.Background(ReleaseAsync(tab, page, requestId, held ? () => gate.Ask(tab, site!, $"send a form to {to}") : null), "ai form post");
	}

	/// <summary>
	/// A document response paused while <see cref="Download"/> waits: the main frame's first final one is let go marked as a
	/// file; a redirect, another frame's, or any after it go on as they are.
	/// </summary>
	private void Responded(TabRef tab, WebView2Page page, string mainFrame, string requestId, JsonElement root)
	{
		var status = root.TryGetProperty("responseStatusCode", out var code) ? code.GetInt32() : 0;
		if (root.GetProperty("frameId").GetString() != mainFrame || !_forcing.TryGetValue(page, out var started) || status is >= 300 and < 400)
		{
			TestHooks.Background(ExpectingGoneAsync(tab, page, "Fetch.continueRequest", new { requestId }), "ai download");
			return;
		}
		_forcing.Remove(page);
		if (root.TryGetProperty("responseErrorReason", out var error) || status >= 400)
		{
			started.TrySetException(new AiToolException(AiErrors.Unsupported, error.ValueKind == JsonValueKind.String ? $"The download failed: {error.GetString()}." : $"The server answered {status}."));
			TestHooks.Background(ExpectingGoneAsync(tab, page, "Fetch.continueRequest", new { requestId }), "ai download");
			return;
		}
		var headers = root.TryGetProperty("responseHeaders", out var list) ? list.EnumerateArray().Select(h => (Name: h.GetProperty("name").GetString()!, Value: h.GetProperty("value").GetString()!)).ToList() : [];
		var disposition = headers.FirstOrDefault(h => h.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)).Value;
		var marked = headers.Where(h => !h.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
			.Select(h => new { name = h.Name, value = h.Value })
			.Append(new { name = "Content-Disposition", value = ForcedDownload.Disposition(disposition) })
			.ToArray();
		TestHooks.Background(ForceAsync(tab, page, started, new { requestId, responseCode = status, responseHeaders = marked }), "ai download");
	}

	private async Task ForceAsync(TabRef tab, WebView2Page page, TaskCompletionSource started, object args)
	{
		try
		{
			using (await CallAsync(tab, page, "Fetch.continueResponse", args)) { }
			started.TrySetResult();
		}
		catch (Exception error)
		{
			started.TrySetException(error);
		}
	}

	/// <summary>Lets a paused request go, after the owner's yes when <paramref name="ask"/> is given; a no aborts the navigation.</summary>
	private async Task ReleaseAsync(TabRef tab, WebView2Page page, string requestId, Func<Task<bool>>? ask)
	{
		var allowed = ask is null || await ask();
		// An unshare meanwhile has already failed it (StopHoldingAsync).
		if (ask is not null && !(_asking.TryGetValue(page, out var ids) && ids.Remove(requestId))) return;
		if (allowed) await ExpectingGoneAsync(tab, page, "Fetch.continueRequest", new { requestId });
		else await ExpectingGoneAsync(tab, page, "Fetch.failRequest", new { requestId, errorReason = "Aborted" });
	}

	/// <summary>
	/// A Fetch call that may find its subject gone: the page or tab closed, or the paused navigation was cancelled or replaced
	/// while the owner decided. WebView2 hands every DevTools error back as E_INVALIDARG with no detail; the request id came from
	/// the browser itself, so for these calls that can only mean the request is gone. Anything else is an error.
	/// </summary>
	private async Task ExpectingGoneAsync(TabRef tab, WebView2Page page, string method, object args)
	{
		if (!page.IsOpen) return;
		try
		{
			using (await CallAsync(tab, page, method, args)) { }
		}
		catch (TabGoneException)
		{
			// The page went with its paused requests.
		}
		catch (Exception error) when (error.HResult == InvalidArgument)
		{
			// The request was cancelled or replaced meanwhile.
		}
	}

	/// <summary>The node's border box in viewport CSS pixels, or null when the page no longer has it or it has no box.</summary>
	private async Task<PixelBox?> BoxAsync(TabRef tab, WebView2Page page, long backendNodeId)
	{
		JsonDocument model;
		try
		{
			model = await CallAsync(tab, page, "DOM.getBoxModel", new { backendNodeId });
		}
		catch (Exception error) when (error is not (TabGoneException or DialogOpenException or CdpTimeoutException))
		{
			// DevTools answers an error for a node that went or isn't rendered.
			return null;
		}
		using (model)
		{
			var quad = model.RootElement.GetProperty("model").GetProperty("border").EnumerateArray().Select(v => v.GetDouble()).ToArray();
			var xs = new[] { quad[0], quad[2], quad[4], quad[6] };
			var ys = new[] { quad[1], quad[3], quad[5], quad[7] };
			return new PixelBox(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
		}
	}

	/// <summary>The tab shows a PDF itself: its document is the viewer's embedder, not a page.</summary>
	private async Task<bool> IsPdfAsync(TabRef tab, WebView2Page page)
	{
		using var tree = await CallAsync(tab, page, "Page.getFrameTree", new { });
		return tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("mimeType").GetString() == "application/pdf";
	}

	/// <summary>Paints <paramref name="boxes"/> (viewport CSS pixels) black on the PNG, whose left top is at <paramref name="x"/>, <paramref name="y"/>.</summary>
	private static async Task<byte[]> PaintAsync(byte[] png, IReadOnlyList<PixelBox> boxes, double x, double y, double cssWidth)
	{
		using var input = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		await input.WriteAsync(png.AsBuffer());
		input.Seek(0);
		var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
		var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
		var bgra = pixels.DetachPixelData();
		var width = (int)decoder.PixelWidth;
		var height = (int)decoder.PixelHeight;
		var scale = width / cssWidth;
		BlackOut.Paint(bgra, width, height, boxes.Select(b => new PixelBox((b.X - x) * scale, (b.Y - y) * scale, b.Width * scale, b.Height * scale)));
		using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
		encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight, (uint)width, (uint)height, decoder.DpiX, decoder.DpiY, bgra);
		await encoder.FlushAsync();
		using var memory = new MemoryStream();
		output.Seek(0);
		await output.AsStreamForRead().CopyToAsync(memory);
		return memory.ToArray();
	}
}
