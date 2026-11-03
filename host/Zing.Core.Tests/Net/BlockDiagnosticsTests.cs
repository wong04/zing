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

	[Theory]
	[InlineData("0.0.0.0", true)]
	[InlineData("127.0.0.53", true)]
	[InlineData("10.1.2.3", true)]
	[InlineData("172.16.5.5", true)]
	[InlineData("172.31.255.255", true)]
	[InlineData("172.15.255.255", false)]
	[InlineData("172.32.0.1", false)]
	[InlineData("169.254.169.254", true)]
	[InlineData("169.253.0.1", false)]
	[InlineData("192.168.0.1", true)]
	[InlineData("192.169.0.1", false)]
	[InlineData("100.64.0.1", false)]
	[InlineData("93.184.216.34", false)]
	[InlineData("::1", true)]
	[InlineData("2606:4700::1111", false)]
	[InlineData("not-an-ip", false)]
	public void Sinkhole_addresses(string ip, bool expected) => Assert.Equal(expected, BlockDiagnostics.IsSinkhole(ip));

	[Fact]
	public void Answers_that_overlap_agree() => Assert.False(BlockDiagnostics.AnswersDiffer(["1.1.1.1", "2.2.2.2"], ["2.2.2.2"]));

	[Fact]
	public void Disjoint_answers_differ() => Assert.True(BlockDiagnostics.AnswersDiffer(["157.240.1.35"], ["157.240.22.174"]));

	[Fact]
	public void No_system_answer_is_not_a_disagreement() => Assert.False(BlockDiagnostics.AnswersDiffer([], ["1.1.1.1"]));

	[Fact]
	public void The_system_probe_target_skips_sinkholes() => Assert.Equal("93.184.216.34", BlockDiagnostics.SystemProbeTarget(["0.0.0.0", "93.184.216.34"]));

	[Fact]
	public void Nothing_to_probe_when_every_answer_is_a_sinkhole() => Assert.Null(BlockDiagnostics.SystemProbeTarget(["0.0.0.0", "10.0.0.1"]));
}
