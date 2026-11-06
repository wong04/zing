namespace Zing.Core.Net;

/// <summary>
/// The lifetime of what a relayed page has asked for: it ends when the page leaves the document or closes, so a closed tab
/// stops asking and a new page doesn't wait behind the last one's files.
/// </summary>
public sealed class DocumentRuns : IDisposable
{
	private CancellationTokenSource _document = new();
	private bool _closed;

	public CancellationToken Current => _document.Token;

	public void LeaveDocument()
	{
		var left = _document;
		_document = new CancellationTokenSource();
		if (_closed) _document.Cancel();
		left.Cancel();
		left.Dispose();
	}

	public void Dispose()
	{
		_closed = true;
		_document.Cancel();
	}
}
