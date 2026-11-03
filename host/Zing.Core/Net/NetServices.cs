namespace Zing.Core.Net;

/// <summary>The network checks and requests Diagnose and Open a Link make, as one bundle a window's API is given.</summary>
/// <param name="Transport">How Open a Link reaches pages; null uses the real network through <paramref name="Doh"/>, a test passes a fake.</param>
public sealed record NetServices(IProbes Probes, IDohClient Doh, IRelayTransport? Transport = null)
{
	private readonly Lazy<IRelayTransport> _relay = new(() => Transport ?? new LiveRelayTransport(Doh));

	/// <summary>The real network. One HttpClient for every lookup, as HttpClient is meant to be used.</summary>
	public static NetServices Live { get; } = new(new Probes(), new DohClient(DohClient.NewHttpClient()));

	private readonly Lazy<IRelayTransport> _relayed = new(() => Transport ?? new LiveRelayTransport(Doh, ConnectPolicy.PublicOnly));

	public IRelayTransport RelayTransport => _relay.Value;

	/// <summary>The transport for a relayed page's own requests, which can't reach the user's own network. A test's fake transport serves both.</summary>
	public IRelayTransport RelayedPageTransport => _relayed.Value;
}
