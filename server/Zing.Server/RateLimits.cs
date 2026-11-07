using System.Net;
using System.Threading.RateLimiting;

namespace Zing.Server;

public sealed record RateLimitOptions(int PerUserPerMinute, int PerIpPerMinute)
{
	public static RateLimitOptions From(IConfiguration configuration) => new(
		Read(configuration, "ZING_RATE_USER_PER_MINUTE", 30),
		Read(configuration, "ZING_RATE_IP_PER_MINUTE", 60));

	private static int Read(IConfiguration configuration, string name, int defaultValue)
	{
		var raw = configuration[name];
		if (raw is null) return defaultValue;
		if (!int.TryParse(raw, out var value) || value <= 0) throw new ConfigurationException($"{name} must be a positive integer.");
		return value;
	}
}

public static class RateLimits
{
	public static PartitionedRateLimiter<HttpContext> Create(RateLimitOptions options) =>
		PartitionedRateLimiter.CreateChained(
			PartitionedRateLimiter.Create<HttpContext, string>(context => PerUser(context, options.PerUserPerMinute)),
			PartitionedRateLimiter.Create<HttpContext, string>(context => PerIp(context, options.PerIpPerMinute)));

	private static RateLimitPartition<string> PerUser(HttpContext context, int limit)
	{
		var sessions = context.RequestServices.GetRequiredService<SessionTokens>();
		if (!sessions.TryValidate(context, out var userId)) return RateLimitPartition.GetNoLimiter("anonymous");
		return RateLimitPartition.GetFixedWindowLimiter($"user:{userId}", _ => Window(limit));
	}

	private static RateLimitPartition<string> PerIp(HttpContext context, int limit)
	{
		var ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
		return RateLimitPartition.GetFixedWindowLimiter($"ip:{ip}", _ => Window(limit));
	}

	private static FixedWindowRateLimiterOptions Window(int limit) => new()
	{
		PermitLimit = limit,
		Window = TimeSpan.FromMinutes(1),
		QueueLimit = 0,
	};
}
