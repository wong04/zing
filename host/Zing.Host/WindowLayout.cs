using Zing.Core;

namespace Zing.Host;

/// <summary>
/// Where things go in one window: its content area and scale, the content's place on screen, the
/// cursor over it, and the shown page's rectangle. The page's position comes from
/// <see cref="ChromeApi.Insets"/> only (AGENTS.md), never from a height of its own.
/// </summary>
public sealed class WindowLayout(HostWindow window, Func<ChromeApi?> api)
{
	/// <summary>The window's pixels per DIP.</summary>
	public double Scale => window.Scale;

	public ContentSize ContentSize => window.ClientSize;

	/// <summary>The content's top-left on screen in DIPs, the units of the renderer's screen points.</summary>
	public Point ContentOrigin
	{
		get
		{
			var origin = window.ClientOrigin;
			var scale = Scale;
			return new Point(origin.X / scale, origin.Y / scale);
		}
	}

	/// <summary>
	/// The cursor over the content while Zing is in front (an overlay of the window in front counts:
	/// the surface or a dropdown with the keyboard is still this window). Test runs never read the real
	/// mouse (AGENTS.md): there it is wherever the test put it (TestHooks "set-cursor"), off the window until then.
	/// </summary>
	public Point? Cursor
	{
		get
		{
			if (TestHooks.IsEndToEndRun) return TestHooks.CursorOver(window);
			if (!window.IsForeground || !Native.GetCursorPos(out var cursor)) return null;
			var origin = ContentOrigin;
			var scale = Scale;
			var point = new Point(cursor.X / scale - origin.X, cursor.Y / scale - origin.Y);
			var size = ContentSize;
			return point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height ? null : point;
		}
	}

	/// <summary>The whole client area, in DIPs.</summary>
	public Rect ContentRect()
	{
		var size = window.ClientSize;
		return new(0, 0, size.Width, size.Height);
	}

	/// <summary>The page goes inside the chrome's insets; nothing to do before the chrome's API exists.</summary>
	public void PlacePage(WebView2Page page)
	{
		if (PageRect() is { } rect) page.Place(rect);
	}

	/// <summary>Where pages go: inside the chrome's insets. Null before the chrome's API exists.</summary>
	public Rect? PageRect()
	{
		if (api() is not { } chrome) return null;
		var insets = chrome.Insets();
		var content = ContentRect();
		return new Rect(insets.Left, insets.Top, Math.Max(0, content.Width - insets.Left - insets.Right), Math.Max(0, content.Height - insets.Top));
	}

	/// <summary>The width the caption buttons take at the chrome's top-right, in whole CSS pixels.</summary>
	public string CaptionInset()
	{
		var inset = window.AppWindow.TitleBar.RightInset / Scale;
		return Math.Ceiling(inset).ToString(System.Globalization.CultureInfo.InvariantCulture);
	}
}
