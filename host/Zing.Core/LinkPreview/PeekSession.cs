namespace Zing.Core.LinkPreview;

/// <param name="Url">The address the layer was opened for, which its page may not have committed yet.</param>
public sealed record PeekLayer(IPage Page, bool IsPopup, string Url)
{
	/// <summary>Where the layer is: its page's address once it has a document, the one it was opened for until then.</summary>
	public string Address => Page.Url is "" or "about:blank" ? Url : Page.Url;
}

public sealed class PeekLayerCapException() : Exception($"A preview holds at most {PeekSession.LayerCap} layers");

/// <summary>A preview's stack of pages over one source tab. Holds pages but never closes them: the caller owns that.</summary>
public sealed class PeekSession(string sourceTabId)
{
	public const int LayerCap = 3;

	private readonly List<PeekLayer> _layers = [];

	public string SourceTabId { get; } = sourceTabId;

	/// <summary>Bottom first.</summary>
	public IReadOnlyList<PeekLayer> Layers => _layers;

	public PeekLayer Top => _layers[^1];

	public bool CanExpand => !Top.IsPopup;

	/// <summary>Popups are exempt from the cap: they are the page's own window, not a link the user chose to follow.</summary>
	public void Push(IPage page, bool isPopup, string url)
	{
		if (!isPopup && _layers.Count >= LayerCap) throw new PeekLayerCapException();
		_layers.Add(new PeekLayer(page, isPopup, url));
	}

	public PeekLayer Pop()
	{
		if (_layers.Count <= 1) throw new InvalidOperationException("Cannot pop the last layer; use CloseAll");
		var top = Top;
		_layers.RemoveAt(_layers.Count - 1);
		return top;
	}

	/// <summary>Empties the session; returns the layers top first.</summary>
	public IReadOnlyList<PeekLayer> CloseAll()
	{
		var closed = Enumerable.Reverse(_layers).ToList();
		_layers.Clear();
		return closed;
	}
}
