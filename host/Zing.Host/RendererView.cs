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
	public static async Task<RendererView> CreateAsync(HostWindow window, CoreWebView2Environment environment, Bridge bridge, string hash, bool overlay)
	{
		// Zing's own UI keeps nothing either, as Electron's zing-chrome partition did: no cache, no storage, no autofill on disk.
		var options = environment.CreateCoreWebView2ControllerOptions();
		options.IsInPrivateModeEnabled = true;
		options.ProfileName = "zing-chrome";
		// The window may close while the engine starts; that is an expected outcome (ViewGoneException), not a crash.
		var view = await HostedView.CreateAsync(window, environment, options, hash, overlay);
		var renderer = new RendererView(view, bridge, hash, window);
		var core = view.Core;
		// CSS app-region:drag moves the window from the chrome, the sign-in screen and the revealed sidebar.
		core.Settings.IsNonClientRegionSupportEnabled = hash is "chrome" or "surface";
		core.Settings.AreDefaultContextMenusEnabled = false;
		core.SetVirtualHostNameToFolderMapping(AppHost, RendererFolder(), CoreWebView2HostResourceAccessKind.Allow);
		core.WebMessageReceived += renderer.OnMessage;
		// Events sent before the root has loaded would be lost, so callers get the view once it has.
		var loaded = new TaskCompletionSource();
		void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			core.NavigationCompleted -= Completed;
			if (e.IsSuccess) loaded.SetResult();
			else loaded.SetException(new RendererLoadException(hash, e.WebErrorStatus.ToString()));
		}
		core.NavigationCompleted += Completed;
		core.Navigate($"https://{AppHost}/index.html#{hash}");
		await loaded.Task;
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

	private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		// Only Zing's own UI may talk to the host, never a web page.
		if (!e.Source.StartsWith($"https://{AppHost}/", StringComparison.Ordinal))
		{
			TestHooks.Record("bridge", new UnauthorizedAccessException($"Message from {e.Source} refused in #{_hash}"));
			return;
		}
		if (TestHooks.TryHandle(e.WebMessageAsJson, _window)) return;
		try
		{
			var reply = await _bridge.HandleAsync(e.WebMessageAsJson);
			// A call can close its own window (window.close); then there is no one left to answer.
			if (IsClosed) return;
			sender.PostWebMessageAsJson(reply);
		}
		catch (Exception error)
		{
			TestHooks.Record("bridge", error);
			throw;
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
