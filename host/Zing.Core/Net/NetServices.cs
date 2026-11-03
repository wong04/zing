namespace Zing.Core.Net;

/// <summary>The network checks Diagnose makes, as one bundle a window's API is given.</summary>
public sealed record NetServices(IProbes Probes, IDohClient Doh)
{
	/// <summary>The real network. One HttpClient for every lookup, as HttpClient is meant to be used.</summary>
	public static NetServices Live { get; } = new(new Probes(), new DohClient(new HttpClient()));
}
