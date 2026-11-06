using System.Text;

namespace Zing.Core.Tests;

public sealed class DropFilesTests
{
	[Fact]
	public void One_file_is_a_20_byte_header_then_its_wide_path_and_a_closing_null()
	{
		var bytes = DropFiles.Build([@"C:\Users\me\a.pdf"]);

		Assert.Equal(20, BitConverter.ToInt32(bytes, 0));
		// pt.x, pt.y and fNC are zero, fWide is 1.
		Assert.Equal(0, BitConverter.ToInt32(bytes, 4));
		Assert.Equal(0, BitConverter.ToInt32(bytes, 8));
		Assert.Equal(0, BitConverter.ToInt32(bytes, 12));
		Assert.Equal(1, BitConverter.ToInt32(bytes, 16));
		var list = Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20);
		Assert.Equal("C:\\Users\\me\\a.pdf\0\0", list);
	}

	[Fact]
	public void Several_files_are_separated_by_a_null_each()
	{
		var bytes = DropFiles.Build([@"C:\a.pdf", @"D:\b c.png"]);

		Assert.Equal("C:\\a.pdf\0D:\\b c.png\0\0", Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20));
	}

	[Fact]
	public void Reading_it_back_gives_the_paths()
	{
		Assert.Equal([@"C:\a.pdf", @"\\pc\share\ü.png"], DropFiles.Parse(DropFiles.Build([@"C:\a.pdf", @"\\pc\share\ü.png"])));
	}

	[Fact]
	public void No_files_is_refused()
	{
		Assert.Throws<ArgumentException>(() => DropFiles.Build([]));
	}

	[Theory]
	[InlineData("a.pdf")]
	[InlineData(@"..\a.pdf")]
	[InlineData("")]
	[InlineData("C:\a\0b.pdf")]
	public void A_path_that_is_not_absolute_or_holds_a_null_is_refused(string path)
	{
		Assert.Throws<ArgumentException>(() => DropFiles.Build([path]));
	}

	[Fact]
	public void A_block_that_is_not_a_wide_file_list_is_not_read()
	{
		var narrow = DropFiles.Build([@"C:\a.pdf"]);
		narrow[16] = 0;

		Assert.Throws<FormatException>(() => DropFiles.Parse(narrow));
		Assert.Throws<FormatException>(() => DropFiles.Parse([1, 2, 3]));
	}
}
