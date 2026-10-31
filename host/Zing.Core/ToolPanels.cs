namespace Zing.Core;

public sealed class PanelNotTearableException(string id) : Exception($"{id} can't be opened in a panel")
{
	public string Id { get; } = id;
}

public sealed class UnknownPanelException(string id) : Exception($"No panel with id {id}")
{
	public string Id { get; } = id;
}

/// <summary>The app's larger features, by the names the Toolbox shows (src/shared/toolbox/panelContent.ts).</summary>
public static class PanelSection
{
	public const string Browser = "Browser";
	public const string Viewer = "Files";
	public const string Convert = "Convert";
	public const string Utilities = "Toolbox";
	public const string Download = "Download";
	public const string Unblock = "Open a Link";
	public const string Diagnose = "Diagnose";
	public const string Network = "Network & VPN";
	public const string Security = "Security";
	public const string Settings = "Settings";

	public static readonly IReadOnlyList<string> All =
		[Browser, Viewer, Convert, Utilities, Download, Unblock, Diagnose, Network, Security, Settings];
}

/// <summary>Single tools, titled as the Toolbox shows them.</summary>
public static class PanelTool
{
	public const string Tools = "Engines";
	public const string Json = "JSON / YAML";
	public const string Base64 = "Base64";
	public const string UrlCode = "URL Encoding";
	public const string NumberBase = "Number Bases";
	public const string Regex = "Regex Tester";
	public const string Hash = "Hashes";
	public const string Hmac = "HMAC";
	public const string Jwt = "JWT Decoder";
	public const string Password = "Passwords";
	public const string CaseConvert = "Text Case";
	public const string TextStats = "Text Stats";
	public const string LinkCleaner = "Link Cleaner";
	public const string Calculator = "Scientific Calculator";
	public const string Graphing = "Graphing Calculator";
	public const string Units = "Unit Converter";
	public const string Timestamp = "Timestamps";
	public const string Cron = "Cron Parser";
	public const string Color = "Color Converter";
	public const string BgRemove = "Background Remover";
	public const string Qr = "QR Code";
	public const string Uuid = "UUID";

	public static readonly IReadOnlyList<string> All =
	[
		Tools, Json, Base64, UrlCode, NumberBase, Regex, Hash, Hmac, Jwt, Password, CaseConvert, TextStats,
		LinkCleaner, Calculator, Graphing, Units, Timestamp, Cron, Color, BgRemove, Qr, Uuid,
	];
}

public enum PanelKind
{
	Section,
	Tool,
}

public readonly record struct PanelSize(double Width, double Height);

/// <summary>What a floating panel, a Toolbox tile or a recents row is showing: a whole section or a single tool.</summary>
public sealed record PanelContent(PanelKind Kind, string Name)
{
	private const string SectionPrefix = "section:";
	private const string ToolPrefix = "tool:";

	/// <summary>The id the recents list stores, such as "tool:Hashes".</summary>
	public string Id => (Kind == PanelKind.Section ? SectionPrefix : ToolPrefix) + Name;

	public string Title => Name;

	public static PanelContent Section(string name) => new(PanelKind.Section, name);

	public static PanelContent Tool(string name) => new(PanelKind.Tool, name);

	/// <summary>Round-trips <see cref="Id"/>. Null for an id that names nothing (a tool removed since it was stored, say).</summary>
	public static PanelContent? FromId(string id)
	{
		if (id.StartsWith(SectionPrefix, StringComparison.Ordinal))
		{
			var name = id[SectionPrefix.Length..];
			return PanelSection.All.Contains(name) ? Section(name) : null;
		}
		if (id.StartsWith(ToolPrefix, StringComparison.Ordinal))
		{
			var name = id[ToolPrefix.Length..];
			return PanelTool.All.Contains(name) ? Tool(name) : null;
		}
		return null;
	}

	/// <summary>The browser owns long-lived web views, so it is not poppable; the Toolbox and Settings are not tools to tear off either.</summary>
	public bool IsTearable => Kind != PanelKind.Section || Name is not (PanelSection.Browser or PanelSection.Utilities or PanelSection.Settings);

	/// <summary>Panels are sized to what they hold.</summary>
	public PanelSize PreferredSize => Kind == PanelKind.Section ? SectionSize(Name) : ToolSize(Name);

	/// <summary>Which features do something with a dropped file (src/shared/shell/dropRouter.ts).</summary>
	public bool AcceptsFiles => Kind == PanelKind.Section ? Name is PanelSection.Viewer or PanelSection.Convert : Name == PanelTool.BgRemove;

	private static PanelSize SectionSize(string name) => name switch
	{
		PanelSection.Viewer or PanelSection.Convert => new(520, 580),
		PanelSection.Settings or PanelSection.Security or PanelSection.Network => new(540, 620),
		PanelSection.Utilities => new(520, 600),
		_ => new(480, 540),
	};

	private static PanelSize ToolSize(string name) => name switch
	{
		PanelTool.Graphing => new(640, 560),
		PanelTool.BgRemove or PanelTool.Qr => new(500, 620),
		PanelTool.Regex or PanelTool.Jwt or PanelTool.Json or PanelTool.TextStats or PanelTool.Hash or PanelTool.Hmac or PanelTool.Base64 => new(520, 580),
		PanelTool.Calculator => new(520, 540),
		_ => new(460, 500),
	};
}

public sealed record PanelRecord(string Id, PanelContent Content, Rect Frame, bool Minimized);

/// <summary>
/// The floating tool panels (src/main/panels.ts, a port of Krackn's PanelStore). It works in window
/// content coordinates and is driven by <see cref="ShellModel"/>, so every rule can be tested directly.
/// </summary>
public sealed class ToolPanels
{
	// How much of a panel must stay inside the area so it can always be grabbed again.
	private const double GrabMargin = 60;
	private const double TopMargin = 6;
	private const double BottomMargin = 40;
	// A panel never fills the area edge to edge.
	private const double AreaPadding = 80;
	private const double MinWidth = 320;
	private const double MinHeight = 300;
	// Opening centres the panel under the cursor, a little below its title bar's middle.
	private const double TitleOffset = 18;

	private List<PanelRecord> _panels = [];
	private string? _frontId;
	private int _next = 1;

	public IReadOnlyList<PanelRecord> Visible => _panels.Where(p => !p.Minimized).ToList();

	public bool IsFull => Visible.Count >= ShellConstants.MaxVisiblePanels;

	public bool Dragging { get; private set; }

	public IReadOnlyList<PanelRecord> All => _panels;

	public ToolPanelsState State() => new(
		_panels.Select(p => new ToolPanelView(p.Id, p.Content.Id, p.Content.Title, p.Frame, p.Minimized)).ToList(),
		_frontId,
		Dragging);

	/// <summary>Keeps <paramref name="size"/> within <paramref name="area"/> by the grab margin, so a dragged panel can hang off the edge but never vanish.</summary>
	public static Point ClampOrigin(Point point, PanelSize size, Rect area)
	{
		var maxX = Math.Max(area.X + area.Width - GrabMargin, area.X);
		var maxY = Math.Max(area.Y + area.Height - BottomMargin, area.Y);
		return new Point(
			Math.Min(Math.Max(point.X, area.X + GrabMargin - size.Width), maxX),
			Math.Min(Math.Max(point.Y, area.Y + TopMargin), maxY));
	}

	public OpenPanelOutcome Open(PanelContent content, Point at, Rect area)
	{
		if (!content.IsTearable) throw new PanelNotTearableException(content.Id);
		var existing = _panels.FirstOrDefault(p => p.Content.Id == content.Id);
		if (existing is { Minimized: true }) return Restore(existing.Id, area);
		if (existing is not null)
		{
			BringToFront(existing.Id);
			return OpenPanelOutcome.Focused;
		}
		if (IsFull) return OpenPanelOutcome.Full;

		var preferred = content.PreferredSize;
		var size = new PanelSize(
			Math.Min(preferred.Width, Math.Max(area.Width - AreaPadding, MinWidth)),
			Math.Min(preferred.Height, Math.Max(area.Height - AreaPadding, MinHeight)));
		var origin = ClampOrigin(new Point(at.X - size.Width / 2, at.Y - TitleOffset), size, area);
		var id = $"panel-{_next++}";
		_panels.Add(new PanelRecord(id, content, Whole(new Rect(origin.X, origin.Y, size.Width, size.Height)), false));
		_frontId = id;
		Settle(id, area);
		return OpenPanelOutcome.Opened;
	}

	public void Close(string id)
	{
		Record(id);
		_panels = _panels.Where(p => p.Id != id).ToList();
		if (_frontId == id) _frontId = Visible.LastOrDefault()?.Id;
	}

	public void Minimize(string id)
	{
		Replace(id, p => p with { Minimized = true });
		if (_frontId == id) _frontId = Visible.LastOrDefault()?.Id;
	}

	public OpenPanelOutcome Restore(string id, Rect area)
	{
		var panel = Record(id);
		if (!panel.Minimized)
		{
			BringToFront(id);
			return OpenPanelOutcome.Focused;
		}
		if (IsFull) return OpenPanelOutcome.Full;
		Replace(id, p => p with { Frame = Clamped(p.Frame, area), Minimized = false });
		BringToFront(id);
		Settle(id, area);
		return OpenPanelOutcome.Restored;
	}

	public void BringToFront(string id)
	{
		var panel = Record(id);
		_panels = [.. _panels.Where(p => p.Id != id), panel];
		_frontId = id;
	}

	public void BeginDrag(string id)
	{
		BringToFront(id);
		Dragging = true;
	}

	/// <summary>
	/// Other panels make room only now, on release. Resolving every frame mid-drag was rejected in
	/// Krackn as distracting; the dragged panel is local view state until this point.
	/// </summary>
	public void EndDrag(string id, Point origin, Rect area)
	{
		Dragging = false;
		Replace(id, p => p with { Frame = Clamped(p.Frame with { X = origin.X, Y = origin.Y }, area) });
		Settle(id, area);
	}

	/// <summary>After the window resizes: nothing may be left beyond reach.</summary>
	public void ClampAll(Rect area) => _panels = _panels.Select(p => p with { Frame = Clamped(p.Frame, area) }).ToList();

	/// <summary>JavaScript's Math.round, which rounds halves toward positive infinity.</summary>
	private static double Round(double value) => Math.Floor(value + 0.5);

	private static Rect Whole(Rect r) => new(Round(r.X), Round(r.Y), Round(r.Width), Round(r.Height));

	private static Rect Clamped(Rect frame, Rect area)
	{
		var origin = ClampOrigin(new Point(frame.X, frame.Y), new PanelSize(frame.Width, frame.Height), area);
		return Whole(frame with { X = origin.X, Y = origin.Y });
	}

	private void Settle(string id, Rect area)
	{
		var shown = Visible;
		var fixedIndex = shown.ToList().FindIndex(p => p.Id == id);
		if (fixedIndex == -1) return;
		var resolved = PanelLayout.ResolveAfterDrop(fixedIndex, shown.Select(p => p.Frame).ToList(), area);
		var frames = shown.Select((p, i) => (p.Id, Frame: Whole(resolved[i]))).ToDictionary(x => x.Id, x => x.Frame);
		_panels = _panels.Select(p => frames.TryGetValue(p.Id, out var frame) ? p with { Frame = frame } : p).ToList();
	}

	private PanelRecord Record(string id) => _panels.FirstOrDefault(p => p.Id == id) ?? throw new UnknownPanelException(id);

	private void Replace(string id, Func<PanelRecord, PanelRecord> change)
	{
		Record(id);
		_panels = _panels.Select(p => p.Id == id ? change(p) : p).ToList();
	}
}

public readonly record struct ContentSize(double Width, double Height);

/// <param name="Content">The window's content size.</param>
/// <param name="Page">Where the page view sits (below the chrome, right of a pinned sidebar).</param>
public sealed record SurfaceInput(
	ContentSize Content,
	Rect Page,
	bool Gated,
	bool SettingsOpen,
	bool SidebarRevealed,
	IReadOnlyList<Rect> PanelFrames,
	bool Dragging);

public static class SurfaceRegion
{
	/// <summary>Room for a panel's soft shadow, which would otherwise be cut off at the view's edge.</summary>
	private const double ShadowRoom = 32;

	/// <summary>
	/// The surface view is sized to what it shows and hidden when it shows nothing (null), so it never
	/// takes clicks meant for the page. It covers the whole window for the gate and the settings sheet,
	/// and the whole page while a panel is dragged (so the pointer stays inside it), and otherwise
	/// hugs the revealed sidebar and the open panels.
	/// </summary>
	public static Rect? Compute(SurfaceInput input)
	{
		var content = input.Content;
		if (input.Gated || input.SettingsOpen) return new Rect(0, 0, content.Width, content.Height);
		var parts = new List<Rect>();
		if (input.SidebarRevealed) parts.Add(new Rect(0, 0, Layout.SidebarColumnWidth, content.Height));
		if (input.Dragging) parts.Add(input.Page);
		else
			foreach (var f in input.PanelFrames)
				parts.Add(new Rect(f.X - ShadowRoom, f.Y - ShadowRoom, f.Width + 2 * ShadowRoom, f.Height + 2 * ShadowRoom));
		if (parts.Count == 0) return null;
		return ClippedToContent(Union(parts), content);
	}

	private static Rect Union(IReadOnlyList<Rect> rects)
	{
		var left = rects.Min(r => r.X);
		var top = rects.Min(r => r.Y);
		var right = rects.Max(r => r.X + r.Width);
		var bottom = rects.Max(r => r.Y + r.Height);
		return new Rect(left, top, right - left, bottom - top);
	}

	private static Rect? ClippedToContent(Rect r, ContentSize content)
	{
		var x = Math.Max(0, Math.Floor(r.X));
		var y = Math.Max(0, Math.Floor(r.Y));
		var right = Math.Min(content.Width, Math.Ceiling(r.X + r.Width));
		var bottom = Math.Min(content.Height, Math.Ceiling(r.Y + r.Height));
		if (right <= x || bottom <= y) return null;
		return new Rect(x, y, right - x, bottom - y);
	}
}
