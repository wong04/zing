using Microsoft.Web.WebView2.Core;
using Zing.Core.Viewer;

namespace Zing.Host;

/// <summary>Turns the viewer's reply into the response WebView2 hands to the page.</summary>
internal static class ViewerResponses
{
	public static CoreWebView2WebResourceResponse Make(CoreWebView2Environment environment, ViewerReply reply)
	{
		var headers = string.Concat(reply.Headers.Select(h => $"{h.Key}: {h.Value}\r\n"));
		return environment.CreateWebResourceResponse(Body(reply), reply.Status, reply.Reason, headers);
	}

	private static Windows.Storage.Streams.IRandomAccessStream? Body(ViewerReply reply)
	{
		// A finished file too big to buffer is read from disk as the engine asks; a growing one never comes this way. The handle is
		// asynchronous, so the engine's reads of it complete without waiting on the thread that asks (the UI thread).
		if (reply.WholeFile is { } path) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan).AsRandomAccessStream();
		if (reply.Body.Length == 0) return null;
		return new MemoryStream(reply.Body, writable: false).AsRandomAccessStream();
	}
}
