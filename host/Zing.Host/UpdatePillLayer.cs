using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The "Relaunch to update" pill (#update-pill): a transparent renderer view at the content's bottom right, sized to what
/// the view measured and shown only while an update is ready, so it never takes clicks meant for the page. The window
/// makes it as it starts with the update state of the moment, so a window opened after the update is ready shows it at
/// once. The view is made only once there is something to show, and closes with the window's scope, even one still loading.
/// </summary>
public sealed class UpdatePillLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Action<RendererView> created, OverlayHost overlays, UpdateState initial)
{
	private const double Margin = 16;

	private Task<RendererView>? _ready;
	private bool _shown = IsReady(initial);
	private double _width;
	private double _height;

	public void Start()
	{
		if (_shown) TestHooks.Background(ShowAsync(), "update pill");
	}

	/// <summary>The app's updater moved on. Raised by Updater.Changed, so it never throws: a failure to make the view is recorded.</summary>
	public void Changed(UpdateState state)
	{
		_shown = IsReady(state);
		if (_ready is null && !_shown) return;
		TestHooks.Background(ShowAsync(), "update pill");
	}

	/// <summary>The view measured the pill; nothing shows until it has.</summary>
	public void SetSize(int width, int height)
	{
		_width = width;
		_height = height;
		Layout();
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		var visible = _shown && _width > 0 && _height > 0;
		if (visible)
		{
			view.Place(Bounds());
			overlays.Show(OverlayLevel.UpdatePill);
		}
		view.Visible = visible;
	}

	private static bool IsReady(UpdateState state) => state.Status == UpdateStatus.Ready;

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private Rect Bounds()
	{
		var area = content();
		return new Rect(area.Width - _width - Margin, area.Height - _height - Margin, _width, _height);
	}

	private async Task ShowAsync()
	{
		var ready = _ready ??= CreateAsync();
		try
		{
			await ready;
		}
		catch (Exception) when (_ready == ready)
		{
			// Not kept: the next change makes the view again rather than failing on this one for the window's life.
			_ready = null;
			throw;
		}
		scope.Checkpoint("update pill");
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "update-pill", overlay: true);
		scope.ThrowIfClosed("update pill");
		overlays.Add(OverlayLevel.UpdatePill, view.View);
		created(view);
		return view;
	}
}
