namespace Zing.Host;

/// <summary>
/// Where an overlay sits among a window's overlays, lowest first. Every overlay is an owned popup: a HostedView's,
/// or a plain window of its own (the preview scrim), so the popups' z-order is the only thing that decides which one is in front.
/// </summary>
public enum OverlayLevel
{
	/// <summary>The dim under a link preview (PeekScrim), below the page UI so find, downloads and prompts draw over a preview.</summary>
	PeekScrim,
	/// <summary>A link preview's buttons.</summary>
	PeekControls,
	/// <summary>The find bar, downloads list and permission prompt (PageUiLayer).</summary>
	PageUi,
	/// <summary>The vault's account picker under a sign-in field (PageUiLayer, #picker).</summary>
	AccountPicker,
	/// <summary>The stack dropdown, #overlay-stack (CoveringLayer).</summary>
	StackDropdown,
	/// <summary>The tab overview, #overlay-overview (CoveringLayer).</summary>
	Overview,
	/// <summary>The account gate, revealed sidebar, tool panels and settings sheet (MainWindow's surface view).</summary>
	Surface,
	/// <summary>The "Relaunch to update" pill (UpdatePillLayer), over the settings sheet where Check now is.</summary>
	UpdatePill,
	/// <summary>Notices (NoticeLayer): the top layer, over everything else.</summary>
	Notices,
}

/// <summary>
/// One per window: the overlays' stacking order in one place. Layers never raise their own popup;
/// they ask here, and anything that must stay above the one shown is put back above it, so the order
/// is always <see cref="OverlayLevel"/>'s, whichever overlay showed last. The order is the one a full layout
/// pass used to leave behind (page UI, stack dropdown, overview, then the surface and the notices over it).
/// The views stay owned by the window's scope (WindowScope); a view closed with it is skipped here. An overlay that isn't a
/// view is added as its raise action, and removed when it goes (the scrim, with its preview).
/// </summary>
public sealed class OverlayHost
{
	private readonly SortedDictionary<OverlayLevel, Action> _raises = [];

	/// <summary>The view an overlay shows in, once it exists.</summary>
	public void Add(OverlayLevel level, HostedView view) => Add(level, view.Raise);

	/// <summary>An overlay that is a window of its own rather than a view (the preview scrim): how to raise it.</summary>
	public void Add(OverlayLevel level, Action raise)
	{
		if (_raises.ContainsKey(level)) throw new InvalidOperationException($"The {level} overlay already has a view");
		_raises[level] = raise;
	}

	/// <summary>An overlay that comes and goes with what it shows (the preview scrim) is gone.</summary>
	public void Remove(OverlayLevel level)
	{
		if (!_raises.Remove(level)) throw new InvalidOperationException($"The {level} overlay has no view");
	}

	/// <summary>Puts the overlay at <paramref name="level"/> above those below it, then re-raises the ones above it, lowest first.</summary>
	public void Show(OverlayLevel level)
	{
		foreach (var (at, raise) in _raises)
			if (at >= level) raise();
	}
}
