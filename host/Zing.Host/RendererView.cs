using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A WebView2 showing one root of Zing's React bundle (#chrome, #overlay,
/// #surface…), the counterpart of renderer-page.ts. Every renderer view of a
/// window talks to that window's one bridge, and only Zing's own pages may.
/// </summary>
public sealed class RendererView
{
	public const string AppHost = "app.zing";

	private readonly Bridge _bridge;
	private readonly string _hash;
	private readonly HostWindow _window;

	private RendererView(HostedView view, Bridge bridge, string hash, HostWindow window)
	{
		View = view;
		_bridge = bridge;
		_hash = hash;
		_window = window;
	}

	public HostedView View { get; }

	/// <summary>Creates the view in <paramref name="window"/> and loads its root; an <paramref name="overlay"/> is transparent and floats above the window.</summary>
	/// <exception cref="ViewGoneException">The window (<paramref name="scope"/>) closed while the view started or loaded.</exception>
	public static async Task<RendererView> CreateAsync(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, string hash, bool overlay)
	{
		// Zing's own UI keeps nothing either, as Electron's zing-chrome partition did: no cache, no storage, no autofill on disk.
		var options = environment.CreateCoreWebView2ControllerOptions();
		options.IsInPrivateModeEnabled = true;
		options.ProfileName = "zing-chrome";
		// The window may close while the engine starts; that is an expected outcome (ViewGoneException), not a crash.
		var view = await HostedView.CreateAsync(window, scope, environment, options, hash, overlay);
		// Once created the scope tracks the view, so a window that closes from here on closes it too.
		scope.ThrowIfClosed(hash);
		var renderer = new RendererView(view, bridge, hash, window);
		var core = view.Core;
		// CSS app-region:drag moves the window from the chrome, the sign-in screen and the revealed sidebar.
		core.Settings.IsNonClientRegionSupportEnabled = hash is "chrome" or "surface";
		core.Settings.AreDefaultContextMenusEnabled = false;
		core.SetVirtualHostNameToFolderMapping(AppHost, RendererFolder(), CoreWebView2HostResourceAccessKind.Allow);
		core.WebMessageReceived += renderer.OnMessage;
		// Events sent before the root has loaded would be lost, so callers get the view once it has.
		// Never resumed inside the engine's event, which may close the window (the e2e close-at hook does).
		var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			core.NavigationCompleted -= Completed;
			if (e.IsSuccess) loaded.TrySetResult();
			else if (scope.IsClosed) loaded.TrySetException(new ViewGoneException(hash));
			else loaded.TrySetException(new RendererLoadException(hash, e.WebErrorStatus.ToString()));
		}
		core.NavigationCompleted += Completed;
		// A closed view never completes its navigation; the scope (which closes the view) ends the wait.
		using var gone = scope.Token.Register(() => loaded.TrySetException(new ViewGoneException(hash)));
		core.Navigate($"https://{AppHost}/index.html#{hash}");
		await loaded.Task;
		scope.Checkpoint(hash);
		return renderer;
	}

	public void Send(string eventJson)
	{
		if (!IsClosed) View.Core.PostWebMessageAsJson(eventJson);
	}

	/// <summary>Places the view at a rectangle of the window's content, in DIPs.</summary>
	public void Place(Rect bounds) => View.Place(bounds);

	public bool Visible
	{
		get => View.Visible;
		set => View.Visible = value;
	}

	public bool IsClosed => View.IsClosed;

	public void Close() => View.Close();

	private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e) =>
		TestHooks.Background(AnswerAsync(sender, e.Source, e.WebMessageAsJson), "bridge");

	/// <summary>Every invoke gets a reply, even one the bridge can't read, so the renderer's promise never hangs.</summary>
	private async Task AnswerAsync(CoreWebView2 sender, string source, string json)
	{
		// Only Zing's own UI may talk to the host, never a web page.
		if (!source.StartsWith($"https://{AppHost}/", StringComparison.Ordinal))
		{
			TestHooks.Record("bridge", new UnauthorizedAccessException($"Message from {source} refused in #{_hash}"));
			return;
		}
		string reply;
		try
		{
			if (TestHooks.TryHandle(json, _window)) return;
			// The bridge turns a handler's failure into an error result itself; it throws only for a message it can't read.
			reply = await _bridge.HandleAsync(json);
		}
		catch (Exception error)
		{
			TestHooks.Record("bridge", error);
			if (ErrorReply(json, error) is not { } failed) return;
			reply = failed;
		}
		// A call can close its own window (window.close); then there is no one left to answer.
		if (IsClosed) return;
		sender.PostWebMessageAsJson(reply);
	}

	/// <summary>The bridge's error result (Bridge.cs) for a malformed message that still has an id; null without one.</summary>
	private static string? ErrorReply(string json, Exception error)
	{
		try
		{
			using var message = System.Text.Json.JsonDocument.Parse(json);
			var root = message.RootElement;
			if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number)) return null;
			return System.Text.Json.JsonSerializer.Serialize(new { kind = "result", id = number, ok = false, error = new { name = error.GetType().Name, message = error.Message } }, ZingJson.Options);
		}
		catch (System.Text.Json.JsonException)
		{
			// Not JSON at all, so no id to answer; the failure is already recorded.
			return null;
		}
	}

	/// <summary>
	/// The built React renderer. ZING_RENDERER_DIR wins (tests, packaging);
	/// otherwise the nearest out/renderer above the app, which is where a
	/// development checkout builds it.
	/// </summary>
	private static string RendererFolder()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_RENDERER_DIR");
		if (!string.IsNullOrEmpty(configured)) return RequireIndex(configured);
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, "out", "renderer");
			if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
		}
		throw new DirectoryNotFoundException("No built renderer: run `npm run build`, or set ZING_RENDERER_DIR");
	}

	private static string RequireIndex(string folder) =>
		File.Exists(Path.Combine(folder, "index.html")) ? folder : throw new DirectoryNotFoundException($"{folder} has no index.html");
}

/// <summary>A Zing view's window closed before the view finished starting.</summary>
public sealed class ViewGoneException(string hash) : Exception($"Zing's #{hash} view went with its window");

public sealed class RendererLoadException(string hash, string status) : Exception($"Zing's #{hash} view failed to load: {status}");
