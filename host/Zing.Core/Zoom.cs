namespace Zing.Core;

/// <summary>
/// Per-tab page zoom: Krackn's steps (BrowserTab.zoomSteps). It scales the whole page, unlike
/// <see cref="TextSize"/>. The engine's own Ctrl+wheel zoom can leave a page between steps, so a
/// step goes to the first one past the current factor, as Krackn's zoomIn/zoomOut do.
/// </summary>
public static class Zoom
{
	public static readonly IReadOnlyList<double> Steps = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];
	public const double Default = 1.0;

	/// <summary>The engine may report a factor a hair off the one it was given.</summary>
	private const double Tolerance = 0.001;

	public static double In(double factor) => Steps.FirstOrDefault(s => s > factor + Tolerance, factor);

	public static double Out(double factor) => Steps.LastOrDefault(s => s < factor - Tolerance, factor);
}
