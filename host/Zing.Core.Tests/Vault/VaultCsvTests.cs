using System.Text;
using System.Text.Json;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>The C# twin of src/shared/import/passwords.ts, held to it by passwords.vectors.json, and the Chrome-format writer for export.</summary>
public sealed class VaultCsvTests : IDisposable
{
	private const string VectorsPath = "src/shared/import/passwords.vectors.json";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vaultcsv-").FullName;

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	public static TheoryData<string> ParseCases() => [.. Vectors.Load(VectorsPath).GetProperty("parse").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	public static TheoryData<string> RefusedCases() => [.. Vectors.Load(VectorsPath).GetProperty("refused").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	private static JsonElement Case(string section, string name) => Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

	[Theory]
	[MemberData(nameof(ParseCases))]
	public void ReadsWhatTheTypeScriptParserReads(string name)
	{
		var vector = Case("parse", name);

		var result = VaultCsv.ParsePasswords(vector.GetProperty("csv").GetString()!);

		Assert.Equal(vector.GetProperty("format").GetString(), result.Format);
		Assert.Equal(
			[.. vector.GetProperty("logins").EnumerateArray().Select(l => new CsvLogin(l.GetProperty("title").GetString()!, l.GetProperty("url").GetString()!, l.GetProperty("username").GetString()!, l.GetProperty("password").GetString()!, l.GetProperty("note").GetString()!))],
			result.Logins);
		Assert.Equal(
			[.. vector.GetProperty("skipped").EnumerateArray().Select(s => new SkippedRow(s.GetProperty("row").GetInt32(), s.GetProperty("reason").GetString()!))],
			result.Skipped);
	}

	[Theory]
	[MemberData(nameof(RefusedCases))]
	public void RefusesWhatTheTypeScriptParserRefuses(string name)
	{
		var vector = Case("refused", name);
		var expected = vector.GetProperty("error").GetString() == "unknown" ? typeof(UnknownPasswordCsvException) : typeof(CsvSyntaxException);

		Assert.Throws(expected, () => VaultCsv.ParsePasswords(vector.GetProperty("csv").GetString()!));
	}

	[Fact]
	public void AFileOverTheLimitIsRefusedByNameBeforeItIsRead()
	{
		var path = Path.Combine(_dir, "big.csv");
		using (var file = File.Create(path)) file.SetLength(VaultCsv.MaxFileBytes + 1);

		var error = Assert.Throws<CsvTooLargeException>(() => VaultCsv.ReadFile(path));

		Assert.Contains("10 MB", error.Message);
	}

	[Fact]
	public void AFileAtTheLimitIsRead()
	{
		var path = Path.Combine(_dir, "edge.csv");
		var header = "name,url,username,password,note\n"u8.ToArray();
		var padding = new byte[VaultCsv.MaxFileBytes - header.Length];
		Array.Fill(padding, (byte)'\n');
		File.WriteAllBytes(path, [.. header, .. padding]);

		Assert.StartsWith("name,url", VaultCsv.ReadFile(path));
	}

	[Fact]
	public void ATextFileThatIsNotUtf8IsRefusedAsBadCsv()
	{
		var path = Path.Combine(_dir, "latin.csv");
		File.WriteAllBytes(path, [0x6e, 0xe9, 0x2c]);

		Assert.Throws<CsvSyntaxException>(() => VaultCsv.ReadFile(path));
	}

	[Fact]
	public void WritesChromesFormatAndQuotesWhatNeedsIt()
	{
		var csv = Encoding.UTF8.GetString(VaultCsv.Write([
			new CsvLogin("plain", "https://a.example", "ada", "pw", ""),
			new CsvLogin("tricky, \"name\"", "https://b.example", "bob", "line\r\nbreak", "two, words"),
		]));

		Assert.Equal("name,url,username,password,note\r\nplain,https://a.example,ada,pw,\r\n\"tricky, \"\"name\"\"\",https://b.example,bob,\"line\r\nbreak\",\"two, words\"\r\n", csv);
	}

	[Fact]
	public void WhatItWritesReadsBackTheSame()
	{
		CsvLogin[] logins =
		[
			new("a", "https://a.example", "ada", "p\"w,1", "note\nwith lines"),
			new("b.example", "http://b.example:8080", "", "plain", ""),
		];

		var result = VaultCsv.ParsePasswords(Encoding.UTF8.GetString(VaultCsv.Write(logins)));

		Assert.Equal(logins, result.Logins);
		Assert.Empty(result.Skipped);
	}
}
