using System.Text.Json;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>The C# source of truth for src/shared/vault/vault.ts, held to it by vault.vectors.json.</summary>
public sealed class VaultModelTests
{
	private const string VectorsPath = "src/shared/vault/vault.vectors.json";

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	// Vectors list only the fields a rule reads; the rest take these.
	private static LoginSummary Summary(JsonElement v) => new(
		Id: v.GetProperty("id").GetString()!,
		Origin: v.TryGetProperty("origin", out var origin) ? origin.GetString()! : "https://a.example",
		Username: v.TryGetProperty("username", out var username) ? username.GetString()! : "u",
		Title: "",
		Note: "",
		CreatedAt: 0,
		UpdatedAt: v.TryGetProperty("updatedAt", out var updated) ? updated.GetInt64() : 0,
		PasswordUpdatedAt: 0,
		DeletedAt: DeletedAt(v));

	private static long? DeletedAt(JsonElement v)
	{
		var deleted = v.GetProperty("deletedAt");
		return deleted.ValueKind == JsonValueKind.Null ? null : deleted.GetInt64();
	}

	private static LoginSummary[] Summaries(JsonElement v) => [.. v.GetProperty("summaries").EnumerateArray().Select(Summary)];

	private static TheoryData<string> Names(string section)
	{
		var data = new TheoryData<string>();
		foreach (var v in Section(section).EnumerateArray()) data.Add(v.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(string section, string name) =>
		Section(section).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

	public static TheoryData<string> OriginCases() => Names("origins");
	public static TheoryData<string> NotWebOriginCases() => Names("notWebOrigins");
	public static TheoryData<string> EntriesCases() => Names("entriesFor");
	public static TheoryData<string> DecisionCases() => Names("saveDecision");

	[Theory]
	[MemberData(nameof(OriginCases))]
	public void An_origin_is_the_scheme_host_and_non_default_port(string name)
	{
		var v = Case("origins", name);
		Assert.Equal(v.GetProperty("origin").GetString(), VaultModel.OriginOf(v.GetProperty("input").GetString()!));
	}

	[Theory]
	[MemberData(nameof(NotWebOriginCases))]
	public void Only_web_pages_have_origins(string name) =>
		Assert.Throws<NotAWebOriginException>(() => VaultModel.OriginOf(Case("notWebOrigins", name).GetProperty("input").GetString()!));

	[Theory]
	[MemberData(nameof(EntriesCases))]
	public void A_frame_is_offered_only_its_own_origins_live_logins(string name)
	{
		var v = Case("entriesFor", name);
		var hits = VaultModel.EntriesFor(Summaries(v), new FillContext(v.GetProperty("topUrl").GetString()!, v.GetProperty("frameUrl").GetString()!));
		Assert.Equal(v.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!), hits.Select(s => s.Id));
	}

	[Theory]
	[MemberData(nameof(DecisionCases))]
	public void A_sign_in_is_saved_updated_or_left_alone(string name)
	{
		var v = Case("saveDecision", name);
		var neverSave = v.GetProperty("neverSave").EnumerateArray().Select(n => new NeverSave(n.GetProperty("origin").GetString()!)).ToArray();
		var decision = VaultModel.SaveDecision(Summaries(v), v.GetProperty("origin").GetString()!, v.GetProperty("username").GetString()!, v.GetProperty("passwordChanged").GetBoolean(), neverSave);
		Assert.Equal(v.GetProperty("expected").GetString(), decision.ToString().ToLowerInvariant());
	}

	[Fact]
	public void Recently_deleted_holds_logins_deleted_under_30_days_ago()
	{
		var retention = Section("retention");
		var now = retention.GetProperty("now").GetInt64();
		var expected = retention.GetProperty("summaries").EnumerateArray().Where(s => s.GetProperty("recentlyDeleted").GetBoolean()).Select(s => s.GetProperty("id").GetString()!);
		Assert.Equal(expected, VaultModel.RecentlyDeleted(Summaries(retention), now).Select(s => s.Id));
	}

	[Fact]
	public void A_login_is_due_for_a_tombstone_once_30_days_have_passed()
	{
		var retention = Section("retention");
		var now = retention.GetProperty("now").GetInt64();
		foreach (var v in retention.GetProperty("summaries").EnumerateArray())
			Assert.Equal(v.GetProperty("dueForTombstone").GetBoolean(), VaultModel.DueForTombstone(Summary(v), now));
	}

	[Fact]
	public void Thirty_days_is_exactly_thirty_times_24_hours() =>
		Assert.Equal(2_592_000_000L, VaultModel.RetentionMs);

	[Fact]
	public void The_privacy_banner_counts_deleted_logins()
	{
		Assert.False(VaultModel.KeepsData([]));
		Assert.True(VaultModel.KeepsData([new LoginSummary("a", "https://a.example", "u", "", "", 0, 0, 0, 1)]));
	}
}
