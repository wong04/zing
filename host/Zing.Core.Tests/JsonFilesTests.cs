using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class JsonFilesTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-settings-").FullName;
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
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromMilliseconds(300), clock);
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
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(10), new ManualScheduler());
		writer.Write(new JsonObject { ["confirmQuit"] = true });
		writer.Flush();
		Assert.True(JsonFiles.LoadSettingsFile(SettingsPath)["confirmQuit"]!.GetValue<bool>());
	}

	[Fact]
	public void FlushWithNothingPendingWritesNothing()
	{
		new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), new ManualScheduler()).Flush();
		Assert.False(File.Exists(SettingsPath));
	}

	[Fact]
	public void FlushCancelsTheTimerSoItWritesOnce()
	{
		var clock = new ManualScheduler();
		var writer = new DebouncedJsonWriter(SettingsPath, TimeSpan.FromSeconds(1), clock);
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
}
