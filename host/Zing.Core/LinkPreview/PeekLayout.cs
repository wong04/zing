namespace Zing.Core.LinkPreview;

/// <summary>Where a preview's page and its button column go, in DIPs.</summary>
public sealed record PeekPlacement(Rect Page, Rect Buttons);

public static class PeekLayout
{
	private const double SideInsetFraction = 0.06;
	private const double MinSideInset = 48;
	private const double VerticalInset = 24;
	private const double MaxWidth = 1400;
	private const double LayerInset = 16;
	private const double ButtonSize = 32;
	private const double ButtonGap = 8;
	private const int ButtonCount = 2;

	public static PeekPlacement Place(Rect pageArea, int depth)
	{
		var layerInset = LayerInset * depth;
		var side = Math.Max(pageArea.Width * SideInsetFraction, MinSideInset) + layerInset;
		var width = Math.Min(pageArea.Width - 2 * side, MaxWidth);
		var page = new Rect(
			pageArea.X + (pageArea.Width - width) / 2,
			pageArea.Y + VerticalInset + layerInset,
			width,
			pageArea.Height - 2 * (VerticalInset + layerInset));
		var buttons = new Rect(page.X + page.Width + ButtonGap, page.Y, ButtonSize, ButtonSize * ButtonCount + ButtonGap);
		return new PeekPlacement(page, buttons);
	}
}
