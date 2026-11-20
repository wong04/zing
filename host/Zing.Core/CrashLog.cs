using System.Globalization;

namespace Zing.Core;

public enum CrashKind { Renderer, Engine }

/// <summary>Settings › About opened the crash log, and there is none.</summary>
public sealed class NoCrashLogException() : InvalidOperationException("There is no crash log: Zing hasn't logged a crash");

/// <summary>
/// The local crash log: one line per crash of Zing's own renderer or of the WebView2 engine, saying when, which, the exit code and
/// Zing's version, never what was open. The last <see cref="MaxLines"/> are kept; nothing is sent anywhere. No file until the first crash.
/// </summary>
public sealed class CrashLog(string path, Func<DateTimeOffset> now, string version)
{
	public const int MaxLines = 200;

	public bool Exists => File.Exists(path);

	/// <summary>The log's file: address, which a tab opens as plain text.</summary>
	public string Address => new Uri(path).AbsoluteUri;

	/// <param name="exitCode">Null when the engine went without a view hearing it, so nothing said how it exited.</param>
	public void Append(CrashKind kind, int? exitCode)
	{
		var line = string.Join('\t',
			now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
			kind.ToString().ToLowerInvariant(),
			exitCode is { } code ? $"exit {code.ToString(CultureInfo.InvariantCulture)} (0x{code:X8})" : "exit unknown",
			$"Zing {version}");
		var lines = Exists ? File.ReadAllLines(path).ToList() : [];
		lines.Add(line);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllLines(path, lines.TakeLast(MaxLines));
	}

	public void Clear() => File.Delete(path);
}
