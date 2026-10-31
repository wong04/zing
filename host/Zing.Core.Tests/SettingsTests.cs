using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public class SettingsTests
{
	private const string VectorsPath = "src/shared/settings/settings.vectors.json";
	private static readonly JsonElement V = Vectors.Load(VectorsPath);

	private static JsonNode? Node(JsonElement e) => JsonNode.Parse(e.GetRawText());

	private static string[] Strings(string name) => V.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

	[Fact]
	public void HasAllKracknKeysPlusZingKeys()
	{
		var krackn = Strings("kracknKeys");
		Assert.Equal(30, krackn.Length);
		Assert.Equal(krackn.Concat(Strings("zingKeys")).Order(), SettingsSchema.Keys.Order());
	}

	[Fact]
	public void EveryDefaultIsAcceptedByItsOwnRule()
	{
		var defaults = SettingsSchema.Defaults();
		var store = new SettingsStore();
		foreach (var key in SettingsSchema.Keys)
		{
			Assert.True(defaults.ContainsKey(key), key);
			Assert.True(SettingsSchema.AcceptsValue(key, defaults[key]), key);
			Assert.True(JsonNode.DeepEquals(defaults[key], store.Get(key)), key);
		}
	}

	[Fact]
	public void KeepsKracknDefaults()
	{
		var defaults = SettingsSchema.Defaults();
		foreach (var p in V.GetProperty("defaultsSubset").EnumerateObject())
			Assert.True(JsonNode.DeepEquals(Node(p.Value), defaults[p.Name]), p.Name);
	}

	[Fact]
	public void ParseReadsAbsentKeysAsDefaults()
	{
		foreach (var c in V.GetProperty("parseAccepted").EnumerateArray())
		{
			var parsed = Settings.Parse(Node(c.GetProperty("input")));
			foreach (var p in c.GetProperty("expectSubset").EnumerateObject())
				Assert.True(JsonNode.DeepEquals(Node(p.Value), parsed[p.Name]), $"{c.GetProperty("name")} {p.Name}");
		}
	}

	[Fact]
	public void ParseRejectsInvalidValuesAndUnknownKeys()
	{
		foreach (var c in V.GetProperty("parseRejected").EnumerateArray())
		{
			var input = Node(c.GetProperty("input"));
			if (c.GetProperty("error").GetString() == "UnknownSetting") Assert.Throws<UnknownSettingException>(() => Settings.Parse(input));
			else Assert.Throws<InvalidSettingException>(() => Settings.Parse(input));
		}
	}

	[Fact]
	public void AcceptsValueMatchesVectors()
	{
		foreach (var c in V.GetProperty("accepts").EnumerateArray())
		{
			var key = c.GetProperty("key").GetString()!;
			var value = Node(c.GetProperty("value"));
			Assert.True(c.GetProperty("ok").GetBoolean() == SettingsSchema.AcceptsValue(key, value), $"{key} {value?.ToJsonString()}");
		}
	}

	[Fact]
	public void AcceptsValueRejectsAnUnknownKey() =>
		Assert.Throws<UnknownSettingException>(() => SettingsSchema.AcceptsValue("history", JsonValue.Create(true)));

	[Fact]
	public void StoreValidatesWritesAndNotifiesListeners()
	{
		var store = new SettingsStore();
		var seen = new List<string>();
		store.Subscribe((_, key) => seen.Add(key));
		store.Set("tabBarPosition", JsonValue.Create("left"));
		Assert.Equal("left", store.Get("tabBarPosition")!.GetValue<string>());
		Assert.Equal(["tabBarPosition"], seen);
		foreach (var c in V.GetProperty("storeRejects").EnumerateArray())
		{
			var key = c.GetProperty("key").GetString()!;
			Assert.Throws<InvalidSettingException>(() => store.Set(key, Node(c.GetProperty("value"))));
			Assert.True(JsonNode.DeepEquals(Node(c.GetProperty("unchanged")), store.Get(key)));
		}
		Assert.Throws<UnknownSettingException>(() => store.Set("history", JsonValue.Create(true)));
	}

	[Fact]
	public void UnsubscribedListenerIsNotNotified()
	{
		var store = new SettingsStore();
		var seen = 0;
		store.Subscribe((_, _) => seen++).Dispose();
		store.Set("restoreTabs", JsonValue.Create(true));
		Assert.Equal(0, seen);
	}

	[Fact]
	public void ChangedKeepsOnlyWhatDiffersFromDefaults()
	{
		foreach (var c in V.GetProperty("changed").EnumerateArray())
		{
			var store = new SettingsStore();
			foreach (var p in c.GetProperty("set").EnumerateObject()) store.Set(p.Name, Node(p.Value));
			Assert.True(JsonNode.DeepEquals(Node(c.GetProperty("changed")), Settings.Changed(store.Snapshot())), c.GetRawText());
		}
	}
}
