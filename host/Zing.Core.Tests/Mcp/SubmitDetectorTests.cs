using System.Text.Json;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class SubmitDetectorTests
{
	[Fact]
	public void MatchesVectors()
	{
		var cases = Vectors.Load("host/Zing.Core.Tests/Mcp/submit.vectors.json").GetProperty("cases").EnumerateArray().ToList();
		Assert.NotEmpty(cases);
		foreach (var c in cases)
		{
			var node = c.GetProperty("node");
			var target = new AxNode(
				node.GetProperty("role").GetString()!,
				node.GetProperty("name").GetString()!,
				node.TryGetProperty("editable", out var editable) && editable.GetBoolean(),
				node.TryGetProperty("inForm", out var inForm) && inForm.GetBoolean(),
				node.TryGetProperty("inputType", out var type) ? type.GetString() : null);
			var action = Enum.Parse<AiAction>(c.GetProperty("action").GetString()!);
			var key = c.TryGetProperty("key", out var k) ? k.GetString() : null;
			Assert.True(c.GetProperty("expected").GetBoolean() == SubmitDetector.IsSubmit(action, target, key), c.ToString());
		}
	}

	[Fact]
	public void VectorWordsAreTheDetectorsWords()
	{
		var words = Vectors.Load("host/Zing.Core.Tests/Mcp/submit.vectors.json").GetProperty("words").EnumerateArray().Select(w => w.GetString()!);
		Assert.Equal(words.Order(), SubmitDetector.Words.Order());
	}
}
