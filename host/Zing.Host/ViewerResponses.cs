using Microsoft.Web.WebView2.Core;
using Zing.Core;
using Zing.Core.Viewer;

namespace Zing.Host;

/// <summary>Turns the viewer's reply into the response WebView2 hands to the page.</summary>
internal static class ViewerResponses
{
	public static CoreWebView2WebResourceResponse Make(CoreWebView2Environment environment, ViewerReply reply)
	{
		try
		{
			var headers = string.Concat(reply.Headers.Select(h => $"{h.Key}: {h.Value}\r\n"));
			return environment.CreateWebResourceResponse(Body(reply), reply.Status, reply.Reason, headers);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// A file locked or removed between the check in the viewer and now: the page gets a status it can read, not a failure of the request.
			reply.Lease?.Dispose();
			Faults.Warning("open a file for the viewer", failure);
			return environment.CreateWebResourceResponse(null, 503, "Service Unavailable", $"Cache-Control: no-store\r\nAccess-Control-Allow-Origin: https://{ZingOrigin.Host}");
		}
		catch
		{
			reply.Lease?.Dispose();
			throw;
		}
	}

	private static Windows.Storage.Streams.IRandomAccessStream? Body(ViewerReply reply)
	{
		// A finished file too big to buffer is read from disk as the engine asks; a growing one never comes this way. The handle is
		// asynchronous, so the engine's reads of it complete without waiting on the thread that asks (the UI thread).
		if (reply.WholeFile is { } path)
		{
			Stream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
			// The hold on the model lasts as long as WebView2 reads the stream and goes when it lets the stream go.
			if (reply.Lease is { } lease) file = new LeasedStream(file, lease);
			return file.AsRandomAccessStream();
		}
		reply.Lease?.Dispose();
		if (reply.Body.Length == 0) return null;
		return new MemoryStream(reply.Body, writable: false).AsRandomAccessStream();
	}

	private sealed class LeasedStream(Stream inner, IDisposable lease) : Stream
	{
		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => inner.CanSeek;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
		public override int Read(Span<byte> buffer) => inner.Read(buffer);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
				lease.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
