namespace Zing.Core.Mcp;

/// <summary>An AI download of a page the engine could show: the response is marked as a file, keeping the server's own name.</summary>
public static class ForcedDownload
{
	/// <summary>The response's Content-Disposition (null when it sent none) with its type made attachment; its parameters, the filename among them, stay.</summary>
	public static string Disposition(string? original)
	{
		if (string.IsNullOrWhiteSpace(original)) return "attachment";
		var parameters = original.IndexOf(';');
		return parameters < 0 ? "attachment" : $"attachment{original[parameters..]}";
	}
}
