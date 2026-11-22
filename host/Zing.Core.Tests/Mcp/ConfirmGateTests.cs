using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class ConfirmGateTests : IDisposable
{
	private static readonly TabRef Tab = new("w1", "t1");
	private static readonly Uri Shop = new("https://shop.example.com/cart");

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-confirm-").FullName;
	private readonly ManualScheduler _scheduler = new();
	private readonly AiShares _shares = new();
	private readonly SitePolicy _policy;
	private readonly ConfirmGate _gate;
	private readonly List<(TabRef, string)> _shown = [];
	private readonly List<TabRef> _closed = [];

	public ConfirmGateTests()
	{
		_policy = new SitePolicy(Path.Combine(_dir, "ai-sites.json"));
		_gate = new ConfirmGate(_scheduler, _shares, _policy);
		_gate.Shown += (tab, text) => _shown.Add((tab, text));
		_gate.Closed += _closed.Add;
		_shares.Share(Tab, Shop);
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public async Task AllowResolvesTrueAndCloses()
	{
		var ask = _gate.Ask(Tab, Shop, "Click Pay now");
		Assert.Equal([(Tab, "Click Pay now")], _shown);
		Assert.Equal([Tab], _gate.Waiting());

		_gate.Answer(Tab, ConfirmAnswer.Allow);

		Assert.True(await ask);
		Assert.Equal([Tab], _closed);
		Assert.Empty(_gate.Waiting());
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public async Task DenyResolvesFalse()
	{
		var ask = _gate.Ask(Tab, Shop, "Click Pay now");
		_gate.Answer(Tab, ConfirmAnswer.Deny);
		Assert.False(await ask);
		Assert.Equal([Tab], _closed);
	}

	[Fact]
	public async Task TimesOutAsDeniedAfterTwoMinutes()
	{
		var ask = _gate.Ask(Tab, Shop, "Click Pay now");

		_scheduler.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
		Assert.False(ask.IsCompleted);

		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.False(await ask);
		Assert.Equal([Tab], _closed);
		Assert.Empty(_gate.Waiting());
	}

	[Fact]
	public async Task UnshareWhileWaitingDenies()
	{
		var ask = _gate.Ask(Tab, Shop, "Click Pay now");

		_shares.Unshare(Tab);

		Assert.False(await ask);
		Assert.Equal([Tab], _closed);
		Assert.Empty(_gate.Waiting());
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public async Task AlwaysAllowSetsSiteNever()
	{
		var ask = _gate.Ask(Tab, Shop, "Click Pay now");

		_gate.Answer(Tab, ConfirmAnswer.AlwaysAllowSite);

		Assert.True(await ask);
		Assert.Equal(SiteLevel.Never, _policy.LevelFor(Shop));
	}

	[Fact]
	public async Task SecondAskOnSameTabDeniesTheFirst()
	{
		var first = _gate.Ask(Tab, Shop, "first");
		var second = _gate.Ask(Tab, Shop, "second");

		Assert.False(await first);
		Assert.False(second.IsCompleted);
		Assert.Equal([Tab], _closed);
		Assert.Equal([(Tab, "first"), (Tab, "second")], _shown);
		Assert.Equal([Tab], _gate.Waiting());

		_gate.Answer(Tab, ConfirmAnswer.Allow);
		Assert.True(await second);
	}

	[Fact]
	public async Task AskOnUnsharedTabDeniesWithoutShowing()
	{
		var other = new TabRef("w1", "t2");

		Assert.False(await _gate.Ask(other, Shop, "Click"));

		Assert.Empty(_shown);
		Assert.Empty(_closed);
	}

	[Fact]
	public void AnswerWithNothingWaitingIsNoOp()
	{
		_gate.Answer(Tab, ConfirmAnswer.Allow);
		Assert.Empty(_closed);
	}
}
