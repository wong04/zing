namespace Zing.Host;

/// <summary>
/// Where an overlay sits among a window's overlays, lowest first. Every overlay is an owned popup
/// (HostedView), so the popups' z-order is the only thing that decides which one is in front.
/// </summary>
public enum OverlayLevel
{
	/// <summary>The find bar, downloads list and permission prompt (PageUiLayer).</summary>
	PageUi,
	/// <summary>The stack dropdown, #overlay-stack (CoveringLayer).</summary>
	StackDropdown,
	/// <summary>The tab overview, #overlay-overview (CoveringLayer).</summary>
	Overview,
	/// <summary>The account gate, revealed sidebar, tool panels and settings sheet (MainWindow's surface view).</summary>
	Surface,
	/// <summary>Notices (NoticeLayer): the top layer, over everything else.</summary>
	Notices,
}

/// <summary>
/// One per window: the overlays' stacking order in one place. Layers never raise their own popup;
/// they ask here, and anything that must stay above the one shown is put back above it, so the order
/// is always <see cref="OverlayLevel"/>'s, whichever overlay showed last. The order is the one a full layout
/// pass used to leave behind (page UI, stack dropdown, overview, then the surface and the notices over it).
/// The views stay owned by the window's scope (WindowScope); a view closed with it is skipped here.
/// </summary>
public sealed class OverlayHost
{
	private readonly SortedDictionary<OverlayLevel, HostedView> _views = [];

	/// <summary>The view an overlay shows in, once it exists.</summary>
	public void Add(OverlayLevel level, HostedView view)
	{
		if (_views.ContainsKey(level)) throw new InvalidOperationException($"The {level} overlay already has a view");
		_views[level] = view;
	}

	/// <summary>Puts the overlay at <paramref name="level"/> above those below it, then re-raises the ones above it, lowest first.</summary>
	public void Show(OverlayLevel level)
	{
		foreach (var (at, view) in _views)
			if (at >= level) view.Raise();
	}
}
