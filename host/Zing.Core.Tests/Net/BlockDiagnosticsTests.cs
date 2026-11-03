using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>The C# twin of src/shared/unblock/blockDiagnostics.ts, held to it by blockDiagnostics.vectors.json.</summary>
public sealed class BlockDiagnosticsTests
{
	private const string VectorsPath = "src/shared/unblock/blockDiagnostics.vectors.json";

	public static TheoryData<string> Combinations()
	{
		var data = new TheoryData<string>();
		foreach (var v in Vectors.Load(VectorsPath).GetProperty("classify").EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	[Fact]
	public void The_vectors_cover_all_32_combinations() => Assert.Equal(32, Vectors.Load(VectorsPath).GetProperty("classify").GetArrayLength());

	[Theory]
	[MemberData(nameof(Combinations))]
	public void Classify_gives_the_same_verdict_as_the_TS(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		var observed = new BlockObservations(
			v.GetProperty("systemTLSOK").GetBoolean(),
			v.GetProperty("sinkholed").GetBoolean(),
			v.GetProperty("systemAnswerEmpty").GetBoolean(),
			v.GetProperty("publicTCPOK").GetBoolean(),
			v.GetProperty("publicTLSOK").GetBoolean());
		Assert.Equal(v.GetProperty("kind").GetString(), JsonSerializer.Serialize(BlockDiagnostics.Classify(observed), ZingJson.Options).Trim('"'));
	}

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	private static TheoryData<string> Cases(string name)
	{
		var data = new TheoryData<string>();
		foreach (var v in Section(name).EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	private static List<string> Strings(JsonElement list) => [.. list.EnumerateArray().Select(a => a.GetString()!)];

	public static TheoryData<string> Sinkholes() => Cases("isSinkhole");

	public static TheoryData<string> Differing() => Cases("answersDiffer");

	public static TheoryData<string> Targets() => Cases("systemProbeTarget");

	[Theory]
	[MemberData(nameof(Sinkholes))]
	public void Sinkhole_addresses_agree_with_the_TS(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		Assert.Equal(v[1].GetBoolean(), BlockDiagnostics.IsSinkhole(v[0].GetString()!));
	}

	[Theory]
	[MemberData(nameof(Differing))]
	public void Differing_answers_agree_with_the_TS(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		Assert.Equal(v.GetProperty("differ").GetBoolean(), BlockDiagnostics.AnswersDiffer(Strings(v.GetProperty("system")), Strings(v.GetProperty("public"))));
	}

	[Theory]
	[MemberData(nameof(Targets))]
	public void The_system_probe_target_agrees_with_the_TS(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		Assert.Equal(v.GetProperty("target").GetString(), BlockDiagnostics.SystemProbeTarget(Strings(v.GetProperty("system"))));
	}
}
