using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>The field-by-field merge against the shared vectors (src/shared/vault/merge.vectors.json). Passwords are labels for ciphertexts.</summary>
public sealed class MergeTests
{
	private const string VectorsPath = "src/shared/vault/merge.vectors.json";

	public static TheoryData<string> Cases()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public void TheMergeGivesTheVectorsAnswer(string name)
	{
		var vectors = JsonNode.Parse(Vectors.Load(VectorsPath).GetRawText())!;
		var login = vectors["login"]!.AsObject();
		var c = vectors["cases"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;

		var merged = Merge.Items(Side(login, c["common"]), Side(login, c["mine"])!, Side(login, c["theirs"])!);

		var expected = Side(login, c["expected"])!;
		Assert.Equal(Describe(expected), Describe(merged));
		AssertNamesItsCiphertexts(merged);
	}

	/// <summary>Ruling 56: the merged summary names the password and each old one by its ciphertext's hash, so none can be swapped.</summary>
	private static void AssertNamesItsCiphertexts(MergeInput merged)
	{
		Assert.Equal(CipherHash.Of(merged.Password), merged.Summary.PasswordHash);
		Assert.Equal(merged.History.Select(CipherHash.Of), merged.Summary.History.Select(h => h.Hash));
	}

	[Fact]
	public void ThePasswordsAreMovedAsTheyAreNeverCopiedOrRebuilt()
	{
		var vectors = JsonNode.Parse(Vectors.Load(VectorsPath).GetRawText())!;
		var login = vectors["login"]!.AsObject();
		var mine = Side(login, JsonNode.Parse("""{ "password": "P2", "passwordVersion": 2, "passwordUpdatedAt": 2000, "updatedAt": 2000, "history": [{ "password": "P1", "version": 1, "replacedAt": 2000 }] }"""))!;
		var theirs = Side(login, JsonNode.Parse("""{ "password": "P3", "passwordVersion": 2, "passwordUpdatedAt": 3000, "updatedAt": 3000, "history": [{ "password": "P1", "version": 1, "replacedAt": 3000 }] }"""))!;

		var merged = Merge.Items(Side(login, new JsonObject()), mine, theirs);

		Assert.Same(theirs.Password, merged.Password);
		Assert.Same(mine.Password, merged.History[0]);
	}

	/// <summary>The side as the merge sees it: <paramref name="login"/> with <paramref name="overrides"/> applied; null for a null side.</summary>
	private static MergeInput? Side(JsonObject login, JsonNode? overrides)
	{
		if (overrides is null) return null;
		var side = login.DeepClone().AsObject();
		foreach (var (key, value) in overrides.AsObject()) side[key] = value?.DeepClone();
		var history = side["history"]!.AsArray();
		var summary = new SummaryPayload(
			Text(side, "origin"), Text(side, "username"), Text(side, "title"), Text(side, "note"),
			Number(side, "createdAt"), Number(side, "updatedAt"), Number(side, "passwordUpdatedAt"),
			side["deletedAt"]?.GetValue<long>(),
			Number(side, "passwordVersion"),
			[.. history.Select(h => new HistoryPayload(Number(h!, "version"), Number(h!, "replacedAt"), CipherHash.Of(Sealed(Text(h!, "password")))))],
			side["neverSave"]!.GetValue<bool>(),
			Generation: Number(side, "generation"),
			PasswordHash: CipherHash.Of(Sealed(Text(side, "password"))));
		return new MergeInput(summary, Sealed(Text(side, "password")), [.. history.Select(h => Sealed(Text(h!, "password")))]);
	}

	private static string Text(JsonNode node, string name) => node[name]!.GetValue<string>();

	private static long Number(JsonNode node, string name) => node[name]!.GetValue<long>();

	private static byte[] Sealed(string label) => Encoding.UTF8.GetBytes(label);

	/// <summary>Everything a merge decides, as one comparable text: the summary's fields and each ciphertext's label.</summary>
	private static string Describe(MergeInput input) => JsonSerializer.Serialize(new
	{
		input.Summary.Origin,
		input.Summary.Username,
		input.Summary.Title,
		input.Summary.Note,
		input.Summary.CreatedAt,
		input.Summary.UpdatedAt,
		input.Summary.PasswordUpdatedAt,
		input.Summary.DeletedAt,
		input.Summary.NeverSave,
		input.Summary.Generation,
		Password = Encoding.UTF8.GetString(input.Password),
		input.Summary.PasswordVersion,
		History = input.Summary.History.Zip(input.History, (entry, sealedBytes) => new { Password = Encoding.UTF8.GetString(sealedBytes), entry.Version, entry.ReplacedAt }),
	});
}
