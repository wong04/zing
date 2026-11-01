using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class JsonFilesTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-settings-").FullName;
	private readonly List<Exception> _writeErrors = [];
	private static readonly DateTime Stamp = new(2026, 3, 4, 5, 6, 7);
	private string SettingsPath => Path.Combine(_dir, "nested", "settings.json");

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public void ReadsDefaultsOnFirstRun() =>
		Assert.True(JsonNode.DeepEquals(SettingsSchema.Defaults(), JsonFiles.LoadSettingsFile(SettingsPath)));

	[Fact]
	public void RoundTripsAChangeThroughADebouncedAtomicWrite()
	{
		var clock = new ManualScheduler();
		var store = new SettingsStore(JsonFiles.LoadSettingsFile(SettingsPath));
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromMilliseconds(300), clock, _writeErrors.Add);
		store.Subscribe((s, _) => writer.Write(Settings.Changed(s)));
		store.Set("tabBarPosition", JsonValue.Create("right"));
		store.Set("restoreTabs", JsonValue.Create(true));
		clock.Advance(TimeSpan.FromMilliseconds(299));
		Assert.False(File.Exists(SettingsPath));
		clock.Advance(TimeSpan.FromMilliseconds(1));

		var written = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"tabBarPosition":"right","restoreTabs":true}"""), written));
		Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!).Select(Path.GetFileName));
		var reloaded = JsonFiles.LoadSettingsFile(SettingsPath);
		Assert.Equal("right", reloaded["tabBarPosition"]!.GetValue<string>());
		Assert.True(reloaded["blockTrackers"]!.GetValue<bool>());
	}

	[Fact]
	public void FlushWritesThePendingChangeImmediately()
	{
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(10), new ManualScheduler(), _writeErrors.Add);
		writer.Write(new JsonObject { ["confirmQuit"] = true });
		writer.Flush();
		Assert.True(JsonFiles.LoadSettingsFile(SettingsPath)["confirmQuit"]!.GetValue<bool>());
	}

	[Fact]
	public void FlushWithNothingPendingWritesNothing()
	{
		new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), new ManualScheduler(), _writeErrors.Add).Flush();
		Assert.False(File.Exists(SettingsPath));
	}

	[Fact]
	public void FlushCancelsTheTimerSoItWritesOnce()
	{
		var clock = new ManualScheduler();
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), clock, _writeErrors.Add);
		writer.Write(new JsonObject());
		writer.Flush();
		Assert.Equal(0, clock.PendingCount);
	}

	[Fact]
	public void RaisesSettingsFileExceptionForACorruptOrInvalidFile()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
		File.WriteAllText(SettingsPath, "{ not json");
		Assert.Throws<SettingsFileException>(() => JsonFiles.LoadSettingsFile(SettingsPath));
		File.WriteAllText(SettingsPath, """{"defaultQuality":"high"}""");
		Assert.Throws<SettingsFileException>(() => JsonFiles.LoadSettingsFile(SettingsPath));
		File.WriteAllText(SettingsPath, """{"history":true}""");
		Assert.Throws<SettingsFileException>(() => JsonFiles.LoadSettingsFile(SettingsPath));
	}

	[Fact]
	public void WriteJsonAtomicReplacesAnExistingFileWithTabIndentedJson()
	{
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["a"] = 1 });
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["a"] = 2 });
		Assert.Equal("{\n\t\"a\": 2\n}\n", File.ReadAllText(SettingsPath).Replace("\r\n", "\n"));
	}

	[Fact]
	public void WriteJsonAtomicLeavesTheOldFileIntactWhenTheWriteFails()
	{
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["a"] = 1 });
		// A directory where the temp file goes makes the write itself fail, as a full disk would.
		var temp = $"{SettingsPath}.{Environment.ProcessId}.tmp";
		Directory.CreateDirectory(temp);
		Assert.True(Record.Exception(() => JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["a"] = 2 })) is IOException or UnauthorizedAccessException);
		Assert.Equal("{\n\t\"a\": 1\n}\n", File.ReadAllText(SettingsPath).Replace("\r\n", "\n"));
	}

	[Fact]
	public void WriteJsonAtomicLeavesNoTempFileBehind()
	{
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["a"] = 1 });
		Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(Path.GetDirectoryName(SettingsPath)!).Select(Path.GetFileName));
	}

	[Fact]
	public void ADebouncedWriteThatFailsIsReportedNotThrown()
	{
		var clock = new ManualScheduler();
		// The settings path is a directory, so the move over it fails.
		Directory.CreateDirectory(SettingsPath);
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), clock, _writeErrors.Add);
		writer.Write(new JsonObject { ["confirmQuit"] = true });
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.True(Assert.Single(_writeErrors) is IOException or UnauthorizedAccessException);
		Assert.Equal(0, clock.PendingCount);
	}

	[Fact]
	public void AFailedWriteIsRetriedByTheNextFlush()
	{
		var clock = new ManualScheduler();
		Directory.CreateDirectory(SettingsPath);
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), clock, _writeErrors.Add);
		writer.Write(new JsonObject { ["confirmQuit"] = true });
		clock.Advance(TimeSpan.FromSeconds(1));
		Directory.Delete(SettingsPath);
		writer.Flush();
		Assert.Single(_writeErrors);
		Assert.True(JsonFiles.LoadSettingsFile(SettingsPath)["confirmQuit"]!.GetValue<bool>());
	}

	[Theory]
	[InlineData("{ not json")]
	[InlineData("""{"defaultQuality":"high"}""")]
	[InlineData("""{"history":true}""")]
	public void ACorruptSettingsFileIsKeptAsideAndZingStartsFromTheDefaults(string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
		File.WriteAllText(SettingsPath, content);

		var loaded = JsonFiles.LoadSettingsOrRecover(SettingsPath, Stamp);

		Assert.True(JsonNode.DeepEquals(SettingsSchema.Defaults(), loaded.Value));
		var problem = Assert.IsType<LoadProblem>(loaded.Problem);
		Assert.IsType<SettingsFileException>(problem.Error);
		Assert.Equal(SettingsPath + ".corrupt-20260304-050607", problem.KeptAs);
		Assert.False(problem.OriginalInPlace);
		Assert.False(File.Exists(SettingsPath));
		Assert.Equal(content, File.ReadAllText(problem.KeptAs!));
	}

	[Fact]
	public void ASecondQuarantineInTheSameSecondKeepsTheFirst()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
		File.WriteAllText(SettingsPath, "first");
		var first = JsonFiles.LoadSettingsOrRecover(SettingsPath, Stamp).Problem!.KeptAs;
		File.WriteAllText(SettingsPath, "second");
		var second = JsonFiles.LoadSettingsOrRecover(SettingsPath, Stamp).Problem!.KeptAs;
		Assert.Equal(SettingsPath + ".corrupt-20260304-050607-2", second);
		Assert.Equal("first", File.ReadAllText(first!));
		Assert.Equal("second", File.ReadAllText(second!));
	}

	[Fact]
	public void AGoodOrMissingSettingsFileHasNoProblem()
	{
		Assert.Null(JsonFiles.LoadSettingsOrRecover(SettingsPath, Stamp).Problem);
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["confirmQuit"] = true });
		var loaded = JsonFiles.LoadSettingsOrRecover(SettingsPath, Stamp);
		Assert.Null(loaded.Problem);
		Assert.True(loaded.Value["confirmQuit"]!.GetValue<bool>());
	}

	[Fact]
	public void ASettingsFileThatCannotBeReadIsLeftInPlaceAndZingStillStarts()
	{
		// As LoadSettingsFile reports a locked or access-denied file: an I/O error, not invalid content.
		JsonFiles.WriteJsonAtomic(SettingsPath, new JsonObject { ["confirmQuit"] = true });
		var locked = new SettingsFileException(SettingsPath, new IOException("in use by another process"));

		var loaded = UserData.Load<JsonObject>(SettingsPath, () => throw locked, SettingsSchema.Defaults, Stamp);

		Assert.True(JsonNode.DeepEquals(SettingsSchema.Defaults(), loaded.Value));
		var problem = Assert.IsType<LoadProblem>(loaded.Problem);
		Assert.Same(locked, problem.Error);
		Assert.True(problem.OriginalInPlace);
		Assert.True(File.Exists(SettingsPath));
		Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(SettingsPath)!, "*.corrupt-*"));
	}

	[Fact]
	public void AnUnexpectedErrorStillRaises() =>
		Assert.Throws<InvalidOperationException>(() => UserData.Load<JsonObject>(SettingsPath, () => throw new InvalidOperationException("bug"), SettingsSchema.Defaults, Stamp));
}
