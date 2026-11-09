namespace Zing.Core.LinkPreview;

/// <summary>
/// A window's last middle press, read once by the next new-window request. Chromium opens a middle-clicked link on the
/// release (auxclick), when the button is already up, so the press is kept for the request that follows it.
/// </summary>
public sealed class MiddlePress
{
	/// <summary>How long after a middle press a new-window request still counts as made by it.</summary>
	public const long WindowMs = 1000;

	private long? _pressedAtMs;

	/// <summary>The middle button went down at <paramref name="nowMs"/> (monotonic milliseconds).</summary>
	public void Pressed(long nowMs) => _pressedAtMs = nowMs;

	/// <summary>Whether the last press made a request at <paramref name="nowMs"/>; the press is used up either way.</summary>
	public bool Take(long nowMs)
	{
		var made = _pressedAtMs is { } at && nowMs >= at && nowMs - at <= WindowMs;
		_pressedAtMs = null;
		return made;
	}
}
