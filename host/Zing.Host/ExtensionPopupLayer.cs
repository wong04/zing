using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// An extension's action popup (its chrome-extension:// page) under the toolbar's right end, in the persistent browsing
/// profile, where the extension runs. One at a time: opening another closes the first. It goes when it loses the keyboard,
/// as a browser's does, on Escape, or when the page closes itself; it is sized to its page once that has loaded, within Chromium's
/// popup limits. A link it opens in a new window opens as a tab instead.
/// </summary>
public sealed class ExtensionPopupLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Func<Rect> content, Func<double> top, OverlayHost overlays, Action<string> openTab)
{
	private const double Margin = 8;
	private const double MinSize = 25;
	private const double MaxWidth = 800;
	private const double MaxHeight = 600;
	private const double StartWidth = 360;
	private const double StartHeight = 480;

	private HostedView? _view;
	private bool _added;
	private double _width = StartWidth;
	private double _height = StartHeight;

	public bool IsOpen => _view is { IsClosed: false };

	public async Task OpenAsync(string url)
	{
		Close();
		_width = StartWidth;
		_height = StartHeight;
		var view = await HostedView.CreateAsync(window, scope, environment, WebView2Page.BrowsingProfile(environment, persistent: true), "extension popup", overlay: true);
		_view = view;
		if (!_added)
		{
			overlays.Add(OverlayLevel.ExtensionPopup, () => _view?.Raise());
			_added = true;
		}
		view.Controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
		var core = view.Core;
		core.WindowCloseRequested += (_, _) => TestHooks.Guard(Close, "extension popup close");
		core.NewWindowRequested += (_, e) => TestHooks.Guard(() => OpenAsTab(e), "extension popup link");
		core.NavigationCompleted += (_, e) => TestHooks.Background(FitAsync(view, e.IsSuccess), "extension popup size");
		// The view gets and loses the keyboard while it starts, so losing it closes the popup only once the page has loaded
		// and then had it with Zing in front; until then a popup would close as soon as it opened.
		var loaded = false;
		var held = false;
		core.NavigationCompleted += (_, _) => loaded = true;
		view.Controller.GotFocus += (_, _) => held |= loaded && window.IsForeground;
		view.Controller.LostFocus += (_, _) => TestHooks.Guard(() =>
		{
			if (held) CloseIf(view);
		}, "extension popup focus");
		view.Controller.AcceleratorKeyPressed += (_, e) => TestHooks.Guard(() => CloseOnEscape(view, e), "extension popup key");
		Layout();
		view.Visible = true;
		overlays.Show(OverlayLevel.ExtensionPopup);
		core.Navigate(url);
		view.Focus();
	}

	public void Close()
	{
		_view?.Close();
		_view = null;
	}

	public void Layout()
	{
		if (_view is not { IsClosed: false } view) return;
		var area = content();
		view.Place(new Rect(Math.Max(0, area.Width - _width - Margin), top() + Margin, _width, _height));
	}

	private void CloseIf(HostedView view)
	{
		if (_view == view) Close();
	}

	private void CloseOnEscape(HostedView view, CoreWebView2AcceleratorKeyPressedEventArgs e)
	{
		const uint Escape = 0x1B;
		if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown || e.VirtualKey != Escape) return;
		e.Handled = true;
		CloseIf(view);
	}

	private void OpenAsTab(CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		openTab(e.Uri);
	}

	private async Task FitAsync(HostedView view, bool loaded)
	{
		if (!loaded || view.IsClosed) return;
		var measured = await view.Core.ExecuteScriptAsync("JSON.stringify([document.documentElement.scrollWidth, document.documentElement.scrollHeight])");
		if (view.IsClosed || _view != view) return;
		var size = JsonSerializer.Deserialize<double[]>(JsonSerializer.Deserialize<string>(measured)!)!;
		_width = Math.Clamp(size[0], MinSize, MaxWidth);
		_height = Math.Clamp(size[1], MinSize, MaxHeight);
		Layout();
		view.Focus();
	}
}
