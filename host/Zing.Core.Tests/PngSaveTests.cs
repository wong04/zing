using Zing.Core.Engines;

namespace Zing.Core.Tests;

/// <summary>The background remover's result: a PNG streamed from the worker is put in Downloads under a plain name, numbered, and never over a file.</summary>
public sealed class PngSaveTests : IDisposable
{
	private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
	private const long Plenty = long.MaxValue;
	private readonly string _root = Directory.CreateTempSubdirectory("zing-pngsave-").FullName;
	private string Downloads => Path.Combine(_root, "downloads");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private static byte[] Png(int extra = 8) => [.. Signature, .. new byte[extra]];

	private async Task<string> Save(string name, byte[] bytes, long? declared = null, long room = Plenty) =>
		(await PngSave.WriteAsync(Downloads, name, new MemoryStream(bytes), declared ?? bytes.Length, room, CancellationToken.None)).Path;

	private string[] Left() => Directory.Exists(Downloads) ? [.. Directory.GetFileSystemEntries(Downloads).Select(p => Path.GetFileName(p)!)] : [];

	[Fact]
	public async Task The_PNG_is_written_under_the_asked_name()
	{
		var path = await Save("cat no background.png", Png());

		Assert.Equal(Path.Combine(Downloads, "cat no background.png"), path);
		Assert.Equal(Png(), File.ReadAllBytes(path));
	}

	[Fact]
	public async Task A_taken_name_is_numbered_and_the_old_file_is_untouched()
	{
		Directory.CreateDirectory(Downloads);
		File.WriteAllText(Path.Combine(Downloads, "cat.png"), "mine");

		var path = await Save("cat.png", Png());

		Assert.Equal(Path.Combine(Downloads, "cat 2.png"), path);
		Assert.Equal("mine", File.ReadAllText(Path.Combine(Downloads, "cat.png")));
	}

	[Fact]
	public async Task No_part_file_is_left_behind()
	{
		await Save("cat.png", Png());

		Assert.Equal(["cat.png"], Left());
	}

	[Theory]
	[InlineData("cat.jpg")]
	[InlineData("cat")]
	[InlineData(".png")]
	[InlineData("..\\cat.png")]
	[InlineData("a/b.png")]
	[InlineData(@"C:\cat.png")]
	[InlineData("cat.png.exe")]
	[InlineData("cat:stream.png")]
	[InlineData("ca*t.png")]
	[InlineData(".hidden.png")]
	[InlineData("tab\there.png")]
	[InlineData("cat.png.")]
	[InlineData("cat.png ")]
	public async Task A_name_that_is_not_a_plain_png_name_is_refused(string name)
	{
		await Assert.ThrowsAsync<PngSaveException>(() => Save(name, Png()));
		Assert.Empty(Left());
	}

	[Fact]
	public async Task A_reserved_device_name_is_saved_under_a_name_Windows_allows()
	{
		var path = await Save("con.png", Png());

		Assert.Equal("con_.png", Path.GetFileName(path));
		Assert.Equal(Png(), File.ReadAllBytes(path));
	}

	[Theory]
	[InlineData("nul.png", "nul_.png")]
	[InlineData("COM1.png", "COM1_.png")]
	[InlineData("cat .png", "cat.png")]
	[InlineData("cat. .png", "cat.png")]
	public async Task A_name_with_a_trailing_dot_or_space_before_the_extension_is_cleaned(string name, string saved)
	{
		Assert.Equal(saved, Path.GetFileName(await Save(name, Png())));
	}

	[Fact]
	public async Task A_name_that_is_too_long_is_refused()
	{
		await Assert.ThrowsAsync<PngSaveException>(() => Save(new string('a', 200) + ".png", Png()));
	}

	[Fact]
	public async Task Bytes_that_are_not_a_PNG_are_refused_and_nothing_is_left()
	{
		await Assert.ThrowsAsync<PngSaveException>(() => Save("cat.png", [1, 2, 3, 4, 5, 6, 7, 8, 9]));
		await Assert.ThrowsAsync<PngSaveException>(() => Save("cat.png", Signature[..4]));
		await Assert.ThrowsAsync<PngSaveException>(() => Save("cat.png", []));
		Assert.Empty(Left());
	}

	[Fact]
	public async Task A_declared_length_over_the_cap_is_refused_before_a_byte_is_read()
	{
		var body = new ThrowingStream();

		await Assert.ThrowsAsync<PngTooLargeException>(() => PngSave.WriteAsync(Downloads, "big.png", body, PngSave.MaxBytes + 1L, Plenty, CancellationToken.None));
		Assert.False(body.Touched);
	}

	[Fact]
	public async Task A_body_longer_than_it_said_is_cut_off_at_the_cap_and_nothing_is_left()
	{
		var lying = new LongStream(PngSave.MaxBytes + 1000L);

		await Assert.ThrowsAsync<PngTooLargeException>(() => PngSave.WriteAsync(Downloads, "big.png", lying, 100, Plenty, CancellationToken.None));
		Assert.Empty(Left());
		Assert.True(lying.Served <= PngSave.MaxBytes + 81920);
	}

	[Fact]
	public async Task The_cap_itself_is_allowed()
	{
		var path = await Save("edge.png", Png(PngSave.MaxBytes - Signature.Length));

		Assert.Equal(PngSave.MaxBytes, new FileInfo(path).Length);
	}

	[Fact]
	public async Task A_picture_that_does_not_fit_the_windows_room_is_refused_plainly()
	{
		var error = await Assert.ThrowsAsync<PngBudgetException>(() => Save("cat.png", Png(1000), room: 500));

		Assert.Contains("as much", error.Message);
		Assert.Empty(Left());
		await Assert.ThrowsAsync<PngBudgetException>(() => PngSave.WriteAsync(Downloads, "cat.png", new MemoryStream(Png(1000)), null, 500, CancellationToken.None));
		Assert.Empty(Left());
	}

	private sealed class ThrowingStream : Stream
	{
		public bool Touched { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) { Touched = true; throw new InvalidOperationException("read"); }
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Touched = true; throw new InvalidOperationException("read"); }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	/// <summary>A PNG signature followed by zeros, <c>length</c> bytes in all, produced as it is read.</summary>
	private sealed class LongStream(long length) : Stream
	{
		private long _position;
		public long Served => _position;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get => _position; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var count = (int)Math.Min(buffer.Length, length - _position);
			buffer.Span[..count].Clear();
			if (_position == 0) Signature.CopyTo(buffer);
			_position += count;
			return ValueTask.FromResult(count);
		}
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
