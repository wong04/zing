using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zing.Core.Net;

/// <summary>The settings Apply changed, as they were: only what Undo needs to put them back.</summary>
public sealed record DnsBackupFile(string Provider, DateTimeOffset SavedAt, IReadOnlyList<DnsAdapter> Adapters, IReadOnlyList<DohEntry> Doh);

/// <summary>
/// The saved previous DNS settings. Zing keeps nothing by default; this file exists because undoing a change to the system needs it,
/// and it is removed after a successful Undo. A file that can't be read is an error, never a guess.
/// </summary>
public static class DnsBackup
{
	private const int Version = 1;

	private static readonly JsonSerializerOptions Strict = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true,
	};

	public static bool Exists(string path) => File.Exists(path);

	public static void Save(string path, DnsBackupFile backup) =>
		JsonFiles.WriteJsonAtomic(path, new { version = Version, provider = backup.Provider, savedAt = backup.SavedAt, adapters = backup.Adapters, doh = backup.Doh });

	public static DnsBackupFile Load(string path)
	{
		if (!File.Exists(path)) throw new NoDnsBackupError();
		try
		{
			var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Strict) ?? throw new JsonException("the file is empty");
			if (stored.Version != Version) throw new JsonException($"it is version {stored.Version}, not {Version}");
			DnsProviders.Get(stored.Provider);
			if (stored.Adapters.Any(a => a is null) || stored.Doh.Any(d => d is null)) throw new JsonException("an entry is empty");
			// The file is on disk where anything of the user's can write, and it becomes a script run as administrator.
			foreach (var adapter in stored.Adapters)
			{
				foreach (var address in adapter.V4) DnsAddresses.Normalize(address, AddressFamily.InterNetwork);
				foreach (var address in adapter.V6 ?? []) DnsAddresses.Normalize(address, AddressFamily.InterNetworkV6);
			}
			foreach (var entry in stored.Doh)
			{
				DnsAddresses.Normalize(entry.Address);
				DnsAddresses.Template(entry.Template);
			}
			return new DnsBackupFile(stored.Provider, stored.SavedAt, stored.Adapters, stored.Doh);
		}
		catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NetError)
		{
			throw new DnsBackupCorruptError(path, error.Message, error);
		}
	}

	/// <summary>Removes the file; one that is already gone is what was wanted.</summary>
	public static void Delete(string path) => File.Delete(path);

	private sealed class Stored
	{
		[JsonRequired] public int Version { get; init; }
		[JsonRequired] public string Provider { get; init; } = "";
		[JsonRequired] public DateTimeOffset SavedAt { get; init; }
		[JsonRequired] public List<DnsAdapter> Adapters { get; init; } = [];
		[JsonRequired] public List<DohEntry> Doh { get; init; } = [];
	}
}
