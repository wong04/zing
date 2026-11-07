using Microsoft.AspNetCore.HttpOverrides;
using Zing.Server;

var builder = WebApplication.CreateBuilder(args);
var config = ServerConfig.Load(builder.Configuration);
var limits = RateLimitOptions.From(builder.Configuration);

var db = new Db(config.DbPath);
db.Initialize();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => new SessionTokens(config.SessionKey, provider.GetRequiredService<TimeProvider>()));
if (config.FakeGoogle)
{
	builder.Services.AddSingleton<FakeGoogleIssuer>();
	builder.Services.AddSingleton<ISigningKeySource>(provider => provider.GetRequiredService<FakeGoogleIssuer>());
}
else
{
	builder.Services.AddSingleton<ISigningKeySource, GoogleKeySource>();
}
builder.Services.AddSingleton<IGoogleTokenValidator>(provider =>
	new GoogleTokenValidator(provider.GetRequiredService<ISigningKeySource>(), config.GoogleClientId));

// Behind Railway's proxy the client address arrives in X-Forwarded-For; trust only the last hop, which the proxy appends.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
	options.ForwardLimit = 1;
	options.KnownNetworks.Clear();
	options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.GlobalLimiter = RateLimits.Create(limits);
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.MapAuth();
app.Run();

public partial class Program;
