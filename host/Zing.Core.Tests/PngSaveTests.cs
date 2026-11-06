using Zing.Core.Engines;

namespace Zing.Core.Tests;

/// <summary>The background remover's result: a PNG from the renderer is put in Downloads under a plain name, numbered, and never over a file.</summary>
public sealed class PngSaveTests : IDisposable
{
	private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
	private readonly string _root = Directory.CreateTempSubdirectory("zing-pngsave-").FullName;
	private string Downloads => Path.Combine(_root, "downloads");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private static byte[] Png(int extra = 8) => [.. Signature, .. new byte[extra]];

	[Fact]
	public void The_PNG_is_written_under_the_asked_name()
	{
		var path = PngSave.Write(Downloads, "cat no background.png", Png());

		Assert.Equal(Path.Combine(Downloads, "cat no background.png"), path);
		Assert.Equal(Png(), File.ReadAllBytes(path));
	}

	[Fact]
	public void A_taken_name_is_numbered_and_the_old_file_is_untouched()
	{
		Directory.CreateDirectory(Downloads);
		File.WriteAllText(Path.Combine(Downloads, "cat.png"), "mine");

		var path = PngSave.Write(Downloads, "cat.png", Png());

		Assert.Equal(Path.Combine(Downloads, "cat 2.png"), path);
		Assert.Equal("mine", File.ReadAllText(Path.Combine(Downloads, "cat.png")));
	}

	[Fact]
	public void No_part_file_is_left_behind()
	{
		PngSave.Write(Downloads, "cat.png", Png());

		Assert.Equal(["cat.png"], Directory.GetFileSystemEntries(Downloads).Select(Path.GetFileName));
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
	public void A_name_that_is_not_a_plain_png_name_is_refused(string name)
	{
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, name, Png()));
		Assert.False(Directory.Exists(Downloads) && Directory.GetFileSystemEntries(Downloads).Length > 0);
	}

	[Fact]
	public void A_name_that_is_too_long_is_refused()
	{
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, new string('a', 200) + ".png", Png()));
	}

	[Fact]
	public void Bytes_that_are_not_a_PNG_are_refused()
	{
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, "cat.png", [1, 2, 3, 4, 5, 6, 7, 8, 9]));
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, "cat.png", Signature[..4]));
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, "cat.png", []));
	}

	[Fact]
	public void Bytes_over_the_cap_are_refused_and_the_cap_itself_is_allowed()
	{
		Assert.Throws<PngSaveException>(() => PngSave.Write(Downloads, "big.png", Png(PngSave.MaxBytes)));
		Assert.Equal(PngSave.MaxBytes, new FileInfo(PngSave.Write(Downloads, "edge.png", Png(PngSave.MaxBytes - Signature.Length))).Length);
	}

	[Fact]
	public void Base64_that_does_not_decode_is_refused()
	{
		Assert.Throws<PngSaveException>(() => PngSave.Decode("not base64 !!"));
	}

	[Fact]
	public void Text_too_long_to_be_under_the_cap_is_refused_before_decoding()
	{
		Assert.Throws<PngSaveException>(() => PngSave.Decode(new string('A', PngSave.MaxBytes * 4 / 3 + 100)));
	}

	[Fact]
	public void Base64_decodes()
	{
		Assert.Equal(Png(), PngSave.Decode(Convert.ToBase64String(Png())));
	}
}
