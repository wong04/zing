using System.Text.RegularExpressions;

namespace Zing.Core.Engines;

/// <summary>The address is not one a Zing view may fetch from the host.</summary>
public sealed class LocalFileRefusedException(string path)
	: ArgumentException($"{path} is not something Zing hands to a view: only the background model by its id, and a file by a token the picker or a drop issued");

/// <summary>A file's bytes and size. Disposing <see cref="Content"/> ends the read (and, for the model, the hold that stops its removal).</summary>
public sealed record LocalFile(Stream Content, long Length);

/// <summary>
/// The bytes a Zing view may fetch from the host without them crossing the JSON bridge: the background model, which is
/// too large to carry as text, by its engine id; and a picked or dropped file by its token, so the page that decodes it
/// needs no path. These are the only two shapes; anything else is refused. Only Zing's own views get an answer (the
/// host attaches this to them alone), so a web page cannot fetch either.
/// </summary>
public sealed partial class LocalFiles(FileGrants grants, EngineStore? engines)
{
	public const string Prefix = "/zing-local/";

	[GeneratedRegex(@"\A/zing-local/(?:model/(?<model>[a-z]{1,16})|file/(?<token>[0-9a-f]{32}))\z", RegexOptions.CultureInvariant)]
	private static partial Regex Shape();

	/// <exception cref="LocalFileRefusedException">The address is not one of the two shapes, or names another engine than the background model.</exception>
	/// <exception cref="UnknownFileGrantException">The token was not issued in this window.</exception>
	/// <exception cref="EngineNotInstalledException">The model has not been downloaded.</exception>
	public LocalFile Open(string path)
	{
		var match = Shape().Match(path);
		if (!match.Success) throw new LocalFileRefusedException(path);
		if (match.Groups["token"].Success) return OpenFile(grants.Resolve(match.Groups["token"].Value));
		if (match.Groups["model"].Value != EngineIds.BackgroundModel) throw new LocalFileRefusedException(path);
		return OpenModel();
	}

	private static LocalFile OpenFile(string path)
	{
		var stream = Read(path);
		return new LocalFile(stream, stream.Length);
	}

	private LocalFile OpenModel()
	{
		var store = engines ?? throw new InvalidOperationException("This host has no engine store");
		var lease = store.Use(EngineIds.BackgroundModel);
		try
		{
			var stream = Read(store.InstalledPath(EngineIds.BackgroundModel));
			return new LocalFile(new LeasedStream(stream, lease), stream.Length);
		}
		catch
		{
			lease.Dispose();
			throw;
		}
	}

	// The person can delete or move the file while it is read, as with any open file in Explorer.
	private static FileStream Read(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);

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
