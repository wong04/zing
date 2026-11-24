using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class StopsBeforeStartsTests
{
	[Fact]
	public async Task AReshareEnablesOnlyAfterTheUnsharesDisable()
	{
		var order = new StopsBeforeStarts<string>();
		var sent = new List<string>();
		var disableAnswered = new TaskCompletionSource();
		order.Stopping("page", StopAsync());

		var start = StartAsync();
		Assert.Empty(sent);
		disableAnswered.SetResult();
		await start;

		Assert.Equal(["Fetch.disable", "Fetch.enable"], sent);

		async Task StopAsync()
		{
			await disableAnswered.Task;
			sent.Add("Fetch.disable");
		}

		async Task StartAsync()
		{
			await order.StoppedAsync("page");
			sent.Add("Fetch.enable");
		}
	}

	[Fact]
	public async Task AStartWaitsForAStopThatReplacedTheOneItWasWaitingFor()
	{
		var order = new StopsBeforeStarts<string>();
		var first = new TaskCompletionSource();
		var second = new TaskCompletionSource();
		order.Stopping("page", first.Task);

		var start = order.StoppedAsync("page");
		order.Stopping("page", second.Task);
		first.SetResult();
		await Task.Yield();
		Assert.False(start.IsCompleted);
		second.SetResult();
		await start;
	}

	[Fact]
	public async Task AFailedStopStillLetsTheStartGo()
	{
		var order = new StopsBeforeStarts<string>();
		order.Stopping("page", Task.FromException(new InvalidOperationException("gone")));
		await order.StoppedAsync("page");
	}

	[Fact]
	public void AKeyNeverStoppedStartsAtOnce() =>
		Assert.True(new StopsBeforeStarts<string>().StoppedAsync("page").IsCompleted);
}
