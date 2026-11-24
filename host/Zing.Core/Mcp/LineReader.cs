using System.Text;

namespace Zing.Core.Mcp;

/// <summary>UTF-8 lines from a stream, each at most <paramref name="maxBytes"/>; StreamReader.ReadLine has no limit.</summary>
public sealed class LineReader(Stream stream, int maxBytes)
{
	private readonly byte[] _chunk = new byte[16 * 1024];
	private readonly MemoryStream _line = new();
	private int _start;
	private int _end;

	/// <summary>Set when <see cref="Next"/> stopped at a line past the limit.</summary>
	public bool TooLong { get; private set; }

	/// <summary>The next line without its \n (or \r\n); null at the end of the stream or at a line that is too long.</summary>
	public async Task<string?> Next(CancellationToken cancel)
	{
		_line.SetLength(0);
		while (true)
		{
			if (_start == _end)
			{
				_start = 0;
				_end = await stream.ReadAsync(_chunk, cancel).ConfigureAwait(false);
				if (_end == 0) return _line.Length > 0 ? Text() : null;
			}
			var newline = Array.IndexOf(_chunk, (byte)'\n', _start, _end - _start);
			var stop = newline < 0 ? _end : newline;
			if (_line.Length + (stop - _start) > maxBytes)
			{
				TooLong = true;
				return null;
			}
			_line.Write(_chunk, _start, stop - _start);
			_start = newline < 0 ? _end : newline + 1;
			if (newline >= 0) return Text();
		}
	}

	private string Text()
	{
		var length = (int)_line.Length;
		if (length > 0 && _line.GetBuffer()[length - 1] == '\r') length--;
		return Encoding.UTF8.GetString(_line.GetBuffer(), 0, length);
	}
}
