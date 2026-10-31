using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
	private Panel _parent = null!;

	public WebView2 View { get; } = new();

	private RendererView(Bridge bridge, string hash)
	{
		_bridge = bridge;
		_hash = hash;
	}

	public bool IsReady => View.CoreWebView2 is not null;

	/// <summary>Creates the view in <paramref name="parent"/> and starts loading its root.</summary>
	public static async Task<RendererView> CreateAsync(Panel parent, CoreWebView2Environment environment, Bridge bridge, string hash, bool transparent)
	{
		var renderer = new RendererView(bridge, hash) { _parent = parent };
		if (transparent) renderer.View.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
		parent.Children.Add(renderer.View);
		// Zing's own UI keeps nothing either, as Electron's zing-chrome partition did: no cache, no storage, no autofill on disk.
		var options = environment.CreateCoreWebView2ControllerOptions();
		options.IsInPrivateModeEnabled = true;
		options.ProfileName = "zing-chrome";
		await renderer.View.EnsureCoreWebView2Async(environment, options);
		// The window may have closed while the engine started; that is an expected outcome, not a crash.
		var core = renderer.View.CoreWebView2 ?? throw new ViewGoneException(hash);
		core.Settings.IsNonClientRegionSupportEnabled = hash == "chrome";
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
		if (!IsClosed) View.CoreWebView2?.PostWebMessageAsJson(eventJson);
	}

	/// <summary>Places the view at a rectangle of the window's content, in DIPs.</summary>
	public void Place(Rect bounds)
	{
		View.HorizontalAlignment = HorizontalAlignment.Left;
		View.VerticalAlignment = VerticalAlignment.Top;
		View.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
		View.Width = Math.Max(0, bounds.Width);
		View.Height = Math.Max(0, bounds.Height);
	}

	public bool Visible
	{
		get => View.Visibility == Visibility.Visible;
		set => View.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
	}

	public bool IsClosed { get; private set; }

	public void Close()
	{
		IsClosed = true;
		View.Close();
	}

	private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		// Only Zing's own UI may talk to the host, never a web page.
		if (!e.Source.StartsWith($"https://{AppHost}/", StringComparison.Ordinal))
		{
			TestHooks.Record("bridge", new UnauthorizedAccessException($"Message from {e.Source} refused in #{_hash}"));
			return;
		}
		if (TestHooks.TryHandle(e.WebMessageAsJson, _parent)) return;
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

