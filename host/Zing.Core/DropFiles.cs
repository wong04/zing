using System.Text;

namespace Zing.Core;

/// <summary>
/// The CF_HDROP clipboard block (a DROPFILES header and a double-null-ended list of wide paths), which is what Explorer
/// pastes as files. Built here, away from any window, so its layout is tested.
/// </summary>
public static class DropFiles
{
	private const int HeaderSize = 20;

	public static byte[] Build(IReadOnlyList<string> paths)
	{
		if (paths.Count == 0) throw new ArgumentException("There are no files to put on the clipboard", nameof(paths));
		foreach (var path in paths)
		{
			if (path.Contains('\0')) throw new ArgumentException("A file path can't hold a null character", nameof(paths));
			if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"'{path}' is not an absolute path", nameof(paths));
		}
		var list = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
		var block = new byte[HeaderSize + list.Length];
		BitConverter.TryWriteBytes(block.AsSpan(0, 4), HeaderSize);
		// pt (8 bytes) and fNC (4) stay zero; fWide says the names are UTF-16.
		BitConverter.TryWriteBytes(block.AsSpan(16, 4), 1);
		list.CopyTo(block, HeaderSize);
		return block;
	}

	/// <exception cref="FormatException">The block isn't a wide file list.</exception>
	public static IReadOnlyList<string> Parse(byte[] block)
	{
		if (block.Length < HeaderSize + 4 || BitConverter.ToInt32(block, 0) != HeaderSize || BitConverter.ToInt32(block, 16) != 1) throw new FormatException("This is not a CF_HDROP block of wide paths");
		var text = Encoding.Unicode.GetString(block, HeaderSize, block.Length - HeaderSize);
		if (!text.EndsWith("\0\0", StringComparison.Ordinal)) throw new FormatException("The file list in this CF_HDROP block doesn't end with two nulls");
		return text[..^2].Split('\0');
	}
}
