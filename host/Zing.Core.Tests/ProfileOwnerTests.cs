namespace Zing.Core.Tests;

public sealed class ProfileOwnerTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private static CancellationToken Cancel => TestContext.Current.CancellationToken;

	private static string NewKey() => "ZingTest-" + Guid.NewGuid().ToString("N");

	/// <summary>A named mutex belongs to a thread, and the same thread may take it again, so each "process" here gets its own.</summary>
	private sealed class Claimant : IDisposable
	{
		private readonly TaskCompletionSource<ProfileOwner?> _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _exit = new();
		private readonly Thread _thread;

		public Claimant(string key)
		{
			_thread = new Thread(() =>
			{
				try
				{
					_claimed.SetResult(ProfileOwner.TryClaim(key));
					_exit.Wait();
				}
				catch (Exception e)
				{
					_claimed.TrySetException(e);
				}
			});
			_thread.Start();
		}

		public Task<ProfileOwner?> Claimed => _claimed.Task;

		/// <summary>The thread ends still holding the mutex, as a killed process does.</summary>
		public void Dispose()
		{
			_exit.Set();
			_thread.Join();
		}
	}

	[Fact]
	public async Task OnlyTheFirstClaimOwnsTheProfile()
	{
		var key = NewKey();
		using var first = new Claimant(key);
		using var second = new Claimant(key);
		Assert.NotNull(await first.Claimed);
		Assert.Null(await second.Claimed);
	}

	[Fact]
	public async Task ClaimsOnDifferentProfilesDoNotMeet()
	{
		using var first = new Claimant(NewKey());
		using var second = new Claimant(NewKey());
		Assert.NotNull(await first.Claimed);
		Assert.NotNull(await second.Claimed);
	}

	[Fact]
	public async Task AnOwnerThatDiedHandsTheProfileOn()
	{
		var key = NewKey();
		var dead = new Claimant(key);
		Assert.NotNull(await dead.Claimed);
		dead.Dispose();
		using var next = new Claimant(key);
		Assert.NotNull(await next.Claimed);
	}

	[Fact]
	public async Task ALaterLaunchDeliversItsArgumentsIntactAndInOrder()
	{
		var key = NewKey();
		using var first = new Claimant(key);
		var owner = (await first.Claimed)!;
		var received = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<string>>();
		using var stop = new CancellationTokenSource();
		var listening = owner.ListenAsync(argv => received.Writer.TryWrite(argv), _ => { }, stop.Token);

		ProfileOwner.HandOver(key, ["https://a.test/?x=1&y=2", "--flag", "two words \"quoted\" é"], Patience);
		ProfileOwner.HandOver(key, [], Patience);

		using var wait = new CancellationTokenSource(Patience);
		Assert.Equal(["https://a.test/?x=1&y=2", "--flag", "two words \"quoted\" é"], await received.Reader.ReadAsync(wait.Token));
		Assert.Empty(await received.Reader.ReadAsync(wait.Token));
		await stop.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
	}

	[Fact]
	public async Task ALaunchDuringTheOwnersStartupWaitsForItToListen()
	{
		var key = NewKey();
		using var first = new Claimant(key);
		var owner = (await first.Claimed)!;
		var handOver = Task.Run(() => ProfileOwner.HandOver(key, ["https://a.test/"], Patience), Cancel);
		await Task.Delay(300, Cancel);
		Assert.False(handOver.IsCompleted);

		var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var stop = new CancellationTokenSource();
		_ = owner.ListenAsync(argv => received.TrySetResult(argv), _ => { }, stop.Token);

		await handOver.WaitAsync(Patience, Cancel);
		Assert.Equal(["https://a.test/"], await received.Task.WaitAsync(Patience, Cancel));
		await stop.CancelAsync();
	}

	[Fact]
	public void HandingOverToAnOwnerThatNeverListensFailsWithATimeout()
	{
		Assert.Throws<TimeoutException>(() => ProfileOwner.HandOver(NewKey(), ["https://a.test/"], TimeSpan.FromMilliseconds(200)));
	}

	[Fact]
	public async Task ABrokenMessageDoesNotStopTheNextLaunch()
	{
		var key = NewKey();
		using var first = new Claimant(key);
		var owner = (await first.Claimed)!;
		var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var broken = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var stop = new CancellationTokenSource();
		_ = owner.ListenAsync(argv => received.TrySetResult(argv), e => broken.TrySetResult(e), stop.Token);

		using (var client = ProfileOwner.Connect(key, Patience))
		{
			await client.WriteAsync("not json"u8.ToArray(), Cancel);
		}
		Assert.IsAssignableFrom<System.Text.Json.JsonException>(await broken.Task.WaitAsync(Patience, Cancel));

		ProfileOwner.HandOver(key, ["https://a.test/"], Patience);
		Assert.Equal(["https://a.test/"], await received.Task.WaitAsync(Patience, Cancel));
		await stop.CancelAsync();
	}
}
