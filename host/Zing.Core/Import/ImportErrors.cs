namespace Zing.Core.Import;

public static class ImportErrors
{
	/// <summary>Import errors reach the renderer, so they never carry the path that .NET puts in its IO messages.</summary>
	/// <param name="what">What couldn't be read, as it reads after "Zing couldn't read": "this profile's bookmarks".</param>
	public static string Explain(Exception error, string what) => error switch
	{
		UnauthorizedAccessException => $"Zing couldn't read {what}: access was denied",
		FileNotFoundException or DirectoryNotFoundException => $"Zing couldn't read {what}: the file is missing",
		IOException => $"Zing couldn't read {what}: the file is in use or unreadable",
		System.Text.Json.JsonException => $"Zing couldn't read {what}: the file is damaged",
		_ => error.Message,
	};
}
