using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>A setting holds a value its rule rejects (src/shared/settings/store.ts InvalidSettingError).</summary>
public sealed class InvalidSettingException(string key, JsonNode? value)
	: Exception($"Invalid value for setting \"{key}\": {value?.ToJsonString() ?? "null"}")
{
	public string Key { get; } = key;
	public JsonNode? Value { get; } = value?.DeepClone();
}

/// <summary>No such setting key (src/shared/settings/store.ts UnknownSettingError).</summary>
public sealed class UnknownSettingException(string key) : Exception($"Unknown setting \"{key}\"")
{
	public string Key { get; } = key;
}

/// <summary>
/// Which keys exist and what each accepts (src/shared/settings/schema.ts). The
/// defaults and per-key rules are generated from the TypeScript schema
/// (Defaults/settings.json, Defaults/settings-schema.json), not restated here.
/// </summary>
public static class SettingsSchema
{
	private static readonly Lazy<JsonObject> DefaultsNode = new(() => ZingJson.LoadDefaults("settings.json").AsObject());
	private static readonly Lazy<JsonObject> Rules = new(() => ZingJson.LoadDefaults("settings-schema.json").AsObject());

	public static IReadOnlyList<string> Keys => Rules.Value.Select(p => p.Key).ToArray();

	/// <summary>A fresh copy, so callers can't alter the shared defaults.</summary>
	public static JsonObject Defaults() => (JsonObject)DefaultsNode.Value.DeepClone();

	public static bool IsSettingKey(string key) => Rules.Value.ContainsKey(key);

	public static bool AcceptsValue(string key, JsonNode? value)
	{
		if (!Rules.Value.TryGetPropertyValue(key, out var rule)) throw new UnknownSettingException(key);
		return Accepts(rule!.AsObject(), JsonSerializer.SerializeToElement(value));
	}

	private static bool Accepts(JsonObject rule, JsonElement value)
	{
		switch (rule["type"]!.GetValue<string>())
		{
			case "enum":
				return value.ValueKind == JsonValueKind.String
					&& rule["values"]!.AsArray().Any(v => v!.GetValue<string>() == value.GetString());
			case "flag":
				return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
			case "number":
				return InRange(value, rule["min"]!.GetValue<double>(), rule["max"]!.GetValue<double>());
			case "integer":
				return InRange(value, rule["min"]!.GetValue<double>(), rule["max"]!.GetValue<double>())
					&& Math.Floor(value.GetDouble()) == value.GetDouble();
			case "text":
				return value.ValueKind == JsonValueKind.String;
			case "textList":
				return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(s => s.ValueKind == JsonValueKind.String);
			case "startShortcuts":
				return value.ValueKind == JsonValueKind.Null
					|| (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(IsShortcut));
			case "sleep":
				return IsSleep(value, rule["min"]!.GetValue<double>(), rule["max"]!.GetValue<double>());
			default:
				throw new InvalidDataException($"settings-schema.json has an unknown rule type: {rule.ToJsonString()}");
		}
	}

	private static bool InRange(JsonElement value, double min, double max) =>
		value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) && d >= min && d <= max;

	private static bool HasString(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;

	private static bool IsShortcut(JsonElement s) =>
		s.ValueKind == JsonValueKind.Object && HasString(s, "title") && HasString(s, "url");

	private static bool IsSleep(JsonElement value, double min, double max)
	{
		if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3) return false;
		return IsBool(value, "enabled") && IsBool(value, "sleepPinned")
			&& value.TryGetProperty("afterMinutes", out var minutes) && InRange(minutes, min, max);
	}

	private static bool IsBool(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False;
}

/// <summary>Whole-object operations on settings (src/shared/settings/store.ts).</summary>
public static class Settings
{
	/// <summary>
	/// Builds a full settings object from stored JSON. Absent keys take their
	/// default; present keys must be known and valid, or this throws rather
	/// than quietly replacing what the user chose.
	/// </summary>
	public static JsonObject Parse(JsonNode? stored)
	{
		if (stored is not JsonObject obj) throw new InvalidSettingException("(root)", stored);
		var values = SettingsSchema.Defaults();
		foreach (var (key, value) in obj)
		{
			if (!SettingsSchema.IsSettingKey(key)) throw new UnknownSettingException(key);
			if (!SettingsSchema.AcceptsValue(key, value)) throw new InvalidSettingException(key, value);
			values[key] = value?.DeepClone();
		}
		return values;
	}

	/// <summary>Only what differs from the defaults, so a later default change reaches users who never touched it.</summary>
	public static JsonObject Changed(JsonObject settings)
	{
		var defaults = SettingsSchema.Defaults();
		var changed = new JsonObject();
		foreach (var key in SettingsSchema.Keys)
		{
			if (!JsonNode.DeepEquals(settings[key], defaults[key])) changed[key] = settings[key]?.DeepClone();
		}
		return changed;
	}
}

public sealed class SettingsStore(JsonObject? initial = null)
{
	public delegate void Listener(JsonObject settings, string key);

	private JsonObject _values = initial is null ? SettingsSchema.Defaults() : (JsonObject)initial.DeepClone();
	private readonly List<Listener> _listeners = [];

	public JsonNode? Get(string key) =>
		(SettingsSchema.IsSettingKey(key) ? _values[key] : throw new UnknownSettingException(key))?.DeepClone();

	public void Set(string key, JsonNode? value)
	{
		if (!SettingsSchema.IsSettingKey(key)) throw new UnknownSettingException(key);
		if (!SettingsSchema.AcceptsValue(key, value)) throw new InvalidSettingException(key, value);
		if (JsonNode.DeepEquals(_values[key], value)) return;
		var next = (JsonObject)_values.DeepClone();
		next[key] = value?.DeepClone();
		_values = next;
		foreach (var listener in _listeners.ToArray()) listener((JsonObject)next.DeepClone(), key);
	}

	public JsonObject Snapshot() => (JsonObject)_values.DeepClone();

	public IDisposable Subscribe(Listener listener)
	{
		_listeners.Add(listener);
		return new Subscription(() => _listeners.Remove(listener));
	}

	private sealed class Subscription(Action remove) : IDisposable
	{
		public void Dispose() => remove();
	}
}
