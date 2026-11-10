using System.Text;
using Zing.Core.Import;

namespace Zing.Core.Vault;

/// <summary>One login row of a password export, as Chrome, Bitwarden, 1Password and Firefox write it.</summary>
public sealed record CsvLogin(string Title, string Url, string Username, string Password, string Note);

/// <summary>A row the import can't use. <paramref name="Row"/> counts the header as row 1.</summary>
public sealed record SkippedRow(int Row, string Reason);

public sealed record PasswordCsv(string Format, IReadOnlyList<CsvLogin> Logins, IReadOnlyList<SkippedRow> Skipped);

public sealed class CsvSyntaxException(string message) : Exception(message);

public sealed class UnknownPasswordCsvException(string message) : Exception(message);

public sealed class CsvTooLargeException() : Exception($"This file is too large to be a password export. Zing reads files of up to {VaultCsv.MaxFileBytes / (1024 * 1024)} MB, which is far more than any real export.");

/// <summary>
/// CSV for the vault's import and export. The reading half is the C# twin of src/shared/import/passwords.ts and csv.ts, held to it by
/// passwords.vectors.json; the writing half is Chrome's format, which Chrome, Edge, Brave and Zing itself read back.
/// </summary>
public static class VaultCsv
{
	/// <summary>A real export is some hundreds of kilobytes at most; a bigger file isn't one, and is refused before it is read.</summary>
	public const int MaxFileBytes = ImportFile.MaxBytes;

	public const string ExportFileName = "Zing Passwords.csv";

	/// <summary>A part file older than this is left over from a crash; the next export removes it.</summary>
	public static readonly TimeSpan PartFileLifetime = TimeSpan.FromMinutes(10);

	private const string PartFilePattern = "*.zing-part";

	private static readonly string[] ChromeHeader = ["name", "url", "username", "password", "note"];

	private sealed record ColumnMap(string? Title, string Url, string Username, string Password, string? Note);

	private static readonly Dictionary<string, ColumnMap> Formats = new()
	{
		["chromium"] = new("name", "url", "username", "password", "note"),
		["bitwarden"] = new("name", "login_uri", "login_username", "login_password", "notes"),
		["onepassword"] = new("title", "url", "username", "password", "notes"),
		["firefox"] = new(null, "url", "username", "password", null),
	};

	/// <exception cref="CsvTooLargeException">Over <see cref="MaxFileBytes"/>.</exception>
	/// <exception cref="CsvSyntaxException">Not UTF-8 text.</exception>
	public static string ReadFile(string path)
	{
		try
		{
			return ImportFile.ReadText(path);
		}
		catch (ImportFileTooLargeException)
		{
			throw new CsvTooLargeException();
		}
		catch (ImportFileNotUtf8Exception)
		{
			throw new CsvSyntaxException("The file isn't UTF-8 text, so it can't be a password export.");
		}
	}

	public static PasswordCsv ParsePasswords(string text)
	{
		var (headers, records) = ParseRecords(text);
		var format = DetectFormat(headers);
		var map = Formats[format];
		List<CsvLogin> logins = [];
		List<SkippedRow> skipped = [];
		for (var index = 0; index < records.Count; index++)
		{
			var record = records[index];
			var row = index + 2;
			if (format == "bitwarden" && record.GetValueOrDefault("type", "") is { Length: > 0 } type && type != "login")
			{
				skipped.Add(new SkippedRow(row, $"Bitwarden {type} item, not a login"));
				continue;
			}
			var url = record[map.Url].Trim();
			var password = record[map.Password];
			if (!IsWebUrl(url))
			{
				skipped.Add(new SkippedRow(row, url.Length > 0 ? $"Not a web address: {url}" : "No web address"));
				continue;
			}
			if (password.Length == 0)
			{
				skipped.Add(new SkippedRow(row, "No password"));
				continue;
			}
			var title = (map.Title is null ? "" : record.GetValueOrDefault(map.Title, "")).Trim();
			logins.Add(new CsvLogin(title.Length > 0 ? title : new Uri(url).Host, url, record[map.Username], password, map.Note is null ? "" : record.GetValueOrDefault(map.Note, "")));
		}
		return new PasswordCsv(format, logins, skipped);
	}

	/// <summary>Chrome's format: a header, then one CRLF-ended row per login, with quotes only where a field needs them.</summary>
	public static byte[] Write(IReadOnlyList<CsvLogin> logins)
	{
		var text = new StringBuilder();
		AppendRow(text, ChromeHeader);
		foreach (var login in logins) AppendRow(text, [login.Title, login.Url, login.Username, login.Password, login.Note]);
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
	}

	/// <summary>
	/// Writes the export into <paramref name="directory"/> as "Zing Passwords.csv", placed as "Never overwrite files" says (see OutputPlacement),
	/// and returns where it went. The passwords go to a hidden part file first, which is removed whatever happens.
	/// </summary>
	public static string SaveExport(OutputPlacement placement, string directory, IReadOnlyList<ExportedLogin> logins)
	{
		var bytes = Write([.. logins.Select(l => new CsvLogin(l.Title.Length > 0 ? l.Title : new Uri(l.Origin).Host, l.Origin, l.Username, l.Password, l.Note))]);
		Directory.CreateDirectory(directory);
		RemoveStalePartFiles(directory);
		var part = Path.Combine(directory, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			// Hidden while it holds the passwords, and normal again before it becomes the export.
			using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write))
			{
				File.SetAttributes(part, FileAttributes.Hidden);
				stream.Write(bytes);
			}
			File.SetAttributes(part, FileAttributes.Normal);
			return placement.Place(part, directory, ExportFileName);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static void RemoveStalePartFiles(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, PartFilePattern))
			if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > PartFileLifetime) File.Delete(file);
	}

	private static void AppendRow(StringBuilder text, string[] fields)
	{
		text.AppendJoin(',', fields.Select(Quote));
		text.Append("\r\n");
	}

	private static string Quote(string field) =>
		field.AsSpan().IndexOfAny(",\"\r\n") < 0 ? field : $"\"{field.Replace("\"", "\"\"")}\"";

	private static string DetectFormat(IReadOnlyList<string> headers)
	{
		bool Has(params string[] names) => names.All(headers.Contains);
		if (Has("login_uri", "login_username", "login_password")) return "bitwarden";
		if (Has("name", "url", "username", "password")) return "chromium";
		if (Has("title", "url", "username", "password")) return "onepassword";
		if (Has("url", "username", "password", "httprealm")) return "firefox";
		throw new UnknownPasswordCsvException($"Unrecognised password export columns: {string.Join(", ", headers)}");
	}

	private static bool IsWebUrl(string value) =>
		Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

	// Rows keyed by lower-cased header names.
	private static (List<string> Headers, List<Dictionary<string, string>> Records) ParseRecords(string text)
	{
		var rows = Parse(text);
		if (rows.Count == 0) throw new CsvSyntaxException("The file is empty");
		var headers = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
		var records = new List<Dictionary<string, string>>();
		for (var index = 1; index < rows.Count; index++)
		{
			var cells = rows[index];
			if (cells.Count != headers.Count) throw new CsvSyntaxException($"Row {index + 1} has {cells.Count} fields, the header has {headers.Count}");
			var record = new Dictionary<string, string>();
			for (var column = 0; column < headers.Count; column++) record[headers[column]] = cells[column];
			records.Add(record);
		}
		return (headers, records);
	}

	// RFC 4180: quoted fields may hold commas, quotes ("") and newlines. Exports from Chrome, Bitwarden and 1Password rely on that for notes.
	internal static List<List<string>> Parse(string text)
	{
		var source = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
		List<List<string>> rows = [];
		List<string> row = [];
		var field = new StringBuilder();
		var quoted = false;
		var i = 0;
		while (i < source.Length)
		{
			var ch = source[i];
			if (quoted)
			{
				if (ch == '"' && i + 1 < source.Length && source[i + 1] == '"')
				{
					field.Append('"');
					i += 2;
					continue;
				}
				if (ch == '"') quoted = false;
				else field.Append(ch);
				i++;
				continue;
			}
			if (ch == '"')
			{
				if (field.Length > 0) throw new CsvSyntaxException($"Unexpected quote inside an unquoted field at offset {i}");
				quoted = true;
				i++;
				continue;
			}
			if (ch == ',')
			{
				row.Add(field.ToString());
				field.Clear();
				i++;
				continue;
			}
			if (ch is '\r' or '\n')
			{
				row.Add(field.ToString());
				rows.Add(row);
				row = [];
				field.Clear();
				i += ch == '\r' && i + 1 < source.Length && source[i + 1] == '\n' ? 2 : 1;
				continue;
			}
			field.Append(ch);
			i++;
		}
		if (quoted) throw new CsvSyntaxException("Unterminated quoted field");
		if (field.Length > 0 || row.Count > 0)
		{
			row.Add(field.ToString());
			rows.Add(row);
		}
		return [.. rows.Where(r => !(r.Count == 1 && r[0].Length == 0))];
	}
}
