using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Zing.Core;
using Zing.Core.Mcp;

namespace Zing.Host;

/// <summary>An AI action that arrives with Task 11 (download and fill_login).</summary>
public sealed class AiActionNotBuiltException(string action) : NotSupportedException($"The AI action {action} isn't built yet.");

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
		long hit;
		using (var found = await CallAsync(tab, page, "DOM.getNodeForLocation", new { x = (int)Math.Round(x), y = (int)Math.Round(y), includeUserAgentShadowDOM = false, ignorePointerEventsNone = true }))
		{
			if (!found.RootElement.TryGetProperty("backendNodeId", out var id)) return null;
			hit = id.GetInt64();
		}
		// The hit test finds the deepest element, such as a button's icon or label span; that is still the button.
		return hit == node || await ContainsAsync(tab, page, node, hit) ? node : hit;
	}

	public async Task Click(TabRef tab, long node)
	{
		var page = await PageAsync(tab);
		await HoldFormPostsAsync(tab, page);
		var (x, y) = await CentreAsync(tab, page, node);
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
	public Task Download(TabRef tab, Uri url) => throw new AiActionNotBuiltException(nameof(Download));
	public Task<(FillResult Result, IReadOnlySet<long> FilledNodes)> FillLogin(TabRef tab) => throw new AiActionNotBuiltException(nameof(FillLogin));

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

	private const string Contains = "function (other) { return this.contains(other) }";

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

	private async Task<bool> ContainsAsync(TabRef tab, WebView2Page page, long outer, long inner)
	{
		var outerId = await ObjectOfAsync(tab, page, outer);
		var innerId = await ObjectOfAsync(tab, page, inner);
		using var answer = await CallAsync(tab, page, "Runtime.callFunctionOn", new { objectId = outerId, functionDeclaration = Contains, arguments = new[] { new { objectId = innerId } }, returnByValue = true });
		return answer.RootElement.GetProperty("result").GetProperty("value").GetBoolean();
	}

	/// <summary>Pages whose document requests pass through <see cref="FormPostBackstop"/>; a page is added before the AI first acts on it.</summary>
	private readonly HashSet<WebView2Page> _holding = [];

	/// <summary>
	/// Pauses the page's document requests in DevTools' Fetch domain, so the backstop can hold a top-level form POST until the
	/// owner answers: NavigationStarting can't wait for an answer, and doesn't say the method. Every other request goes on at once.
	/// </summary>
	private async Task HoldFormPostsAsync(TabRef tab, WebView2Page page)
	{
		_holding.RemoveWhere(p => !p.IsOpen);
		if (_holding.Contains(page)) return;
		string mainFrame;
		using (var tree = await CallAsync(tab, page, "Page.getFrameTree", new { }))
			mainFrame = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
		EnsureCurrent(tab, page);
		if (!_holding.Add(page)) return;
		page.OnDevToolsEvent("Fetch.requestPaused", json => Paused(tab, page, mainFrame, json));
		using (await CallAsync(tab, page, "Fetch.enable", new { patterns = new[] { new { urlPattern = "*", resourceType = "Document", requestStage = "Request" } } })) { }
	}

	private void Paused(TabRef tab, WebView2Page page, string mainFrame, string json)
	{
		using var paused = JsonDocument.Parse(json);
		var root = paused.RootElement;
		var requestId = root.GetProperty("requestId").GetString()!;
		var request = root.GetProperty("request");
		// A closed tab is unshared first, so only a live tab's address is asked for.
		var site = shares.IsShared(tab) ? UrlOf(tab) : null;
		var held = site is not null && backstop.Holds(tab, site, request.GetProperty("method").GetString()!, root.GetProperty("frameId").GetString() == mainFrame);
		var to = Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var target) ? target.IdnHost : "another page";
		TestHooks.Background(ReleaseAsync(page, requestId, held ? () => gate.Ask(tab, site!, $"send a form to {to}") : null), "ai form post");
	}

	/// <summary>Lets a paused request go, after the owner's yes when <paramref name="ask"/> is given; a no aborts the navigation.</summary>
	private static async Task ReleaseAsync(WebView2Page page, string requestId, Func<Task<bool>>? ask)
	{
		var allowed = ask is null || await ask();
		// A page that closed while the owner was asked took its paused request with it.
		if (!page.IsOpen) return;
		if (allowed) await page.DevToolsAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId }));
		else await page.DevToolsAsync("Fetch.failRequest", JsonSerializer.Serialize(new { requestId, errorReason = "Aborted" }));
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
