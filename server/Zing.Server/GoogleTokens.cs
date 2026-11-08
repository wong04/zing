using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Zing.Server;

public sealed class InvalidIdTokenException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GoogleIdentity(string Sub);

public interface IGoogleTokenValidator
{
	Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken cancellation);
}

// The only thing that differs between production and tests is where the signing keys come from.
public interface ISigningKeySource
{
	Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken cancellation);

	// Re-reads the keys now (an unknown kid means they may have rotated) and returns them.
	// The source throttles this; inside the throttle window it returns the keys it already has.
	Task<IReadOnlyCollection<SecurityKey>> RefreshKeysAsync(CancellationToken cancellation);
}

public sealed class GoogleKeySource : ISigningKeySource
{
	public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan ForcedKeysLifetime = TimeSpan.FromHours(1);
	private const string Discovery = "https://accounts.google.com/.well-known/openid-configuration";

	private readonly TimeProvider clock;
	private readonly Func<CancellationToken, Task<OpenIdConnectConfiguration>> fetch;
	private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration;
	private readonly SemaphoreSlim refreshLock = new(1, 1);
	private IReadOnlyCollection<SecurityKey>? forcedKeys;
	private DateTimeOffset forcedAt = DateTimeOffset.MinValue;
	private DateTimeOffset lastFetch = DateTimeOffset.MinValue;

	public GoogleKeySource(TimeProvider clock, Func<CancellationToken, Task<OpenIdConnectConfiguration>>? fetch = null)
	{
		this.clock = clock;
		this.fetch = fetch ?? (cancellation => OpenIdConnectConfigurationRetriever.GetAsync(Discovery, new HttpDocumentRetriever(), cancellation));
		configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
			Discovery,
			new OpenIdConnectConfigurationRetriever(),
			new HttpDocumentRetriever())
		{
			AutomaticRefreshInterval = TimeSpan.FromHours(1),
		};
	}

	public async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken cancellation)
	{
		var forced = forcedKeys;
		if (forced is not null && clock.GetUtcNow() - forcedAt < ForcedKeysLifetime) return forced;

		var document = await configuration.GetConfigurationAsync(cancellation);
		return document.SigningKeys.ToList();
	}

	public async Task<IReadOnlyCollection<SecurityKey>> RefreshKeysAsync(CancellationToken cancellation)
	{
		await refreshLock.WaitAsync(cancellation);
		try
		{
			var now = clock.GetUtcNow();
			if (now - lastFetch < MinRefreshInterval) return await GetKeysAsync(cancellation);

			var document = await fetch(cancellation);
			lastFetch = now;
			forcedKeys = document.SigningKeys.ToList();
			forcedAt = now;
			return forcedKeys;
		}
		finally
		{
			refreshLock.Release();
		}
	}
}

public sealed class FakeGoogleIssuer : ISigningKeySource, IDisposable
{
	private readonly List<RSA> rsas = [];
	private readonly List<SecurityKey> published = [];
	private readonly List<SecurityKey> pending = [];
	private readonly object gate = new();
	private int refreshCount;

	public FakeGoogleIssuer()
	{
		SigningKey = NewKey();
		published.Add(SigningKey);
	}

	public SecurityKey SigningKey { get; private set; }

	public int RefreshCount => refreshCount;

	// A new key stays unpublished until the validator asks for a refresh, like a rotation behind a key cache.
	public SecurityKey Rotate()
	{
		lock (gate)
		{
			SigningKey = NewKey();
			pending.Add(SigningKey);
			return SigningKey;
		}
	}

	public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken cancellation)
	{
		lock (gate) return Task.FromResult<IReadOnlyCollection<SecurityKey>>(published.ToList());
	}

	public Task<IReadOnlyCollection<SecurityKey>> RefreshKeysAsync(CancellationToken cancellation)
	{
		Interlocked.Increment(ref refreshCount);
		lock (gate)
		{
			published.AddRange(pending);
			pending.Clear();
			return Task.FromResult<IReadOnlyCollection<SecurityKey>>(published.ToList());
		}
	}

	public void Dispose()
	{
		foreach (var rsa in rsas) rsa.Dispose();
	}

	private RsaSecurityKey NewKey()
	{
		var rsa = RSA.Create(2048);
		rsas.Add(rsa);
		return new RsaSecurityKey(rsa) { KeyId = $"fake-google-key-{rsas.Count}" };
	}
}

public sealed class GoogleTokenValidator(ISigningKeySource keys, string clientId) : IGoogleTokenValidator
{
	private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];
	private readonly JsonWebTokenHandler handler = new();

	public async Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken cancellation)
	{
		var keyId = ReadKeyId(idToken);
		var signingKeys = await keys.GetKeysAsync(cancellation);
		if (keyId is not null && !signingKeys.Any(key => key.KeyId == keyId))
		{
			signingKeys = await keys.RefreshKeysAsync(cancellation);
		}

		var parameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidIssuers = Issuers,
			ValidateAudience = true,
			ValidAudience = clientId,
			ValidateLifetime = true,
			RequireExpirationTime = true,
			ClockSkew = TimeSpan.FromMinutes(1),
			ValidateIssuerSigningKey = true,
			RequireSignedTokens = true,
			ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
			IssuerSigningKeys = signingKeys,
		};

		var result = await handler.ValidateTokenAsync(idToken, parameters);
		if (!result.IsValid) throw new InvalidIdTokenException("ID token rejected.", result.Exception);

		var token = (JsonWebToken)result.SecurityToken;
		if (string.IsNullOrEmpty(token.Subject)) throw new InvalidIdTokenException("ID token has no sub.");
		if (!token.TryGetClaim("email_verified", out var verified) || !string.Equals(verified.Value, "true", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidIdTokenException("ID token email is not verified.");
		}
		return new GoogleIdentity(token.Subject);
	}

	private static string? ReadKeyId(string idToken)
	{
		try
		{
			return new JsonWebToken(idToken).Kid;
		}
		catch (ArgumentException error)
		{
			throw new InvalidIdTokenException("ID token is not a JWT.", error);
		}
	}
}

public sealed record FakeTokenRequest(string? Sub, string? Email);

// Only mapped with ZING_SERVER_FAKE_GOOGLE=1, which the Production environment refuses (ServerConfig): Zing's e2e runs and
// host tests ask it for an ID token in place of Google, so no test signs in with a real account.
public static class FakeGoogleEndpoints
{
	public static void MapFakeGoogle(this IEndpointRouteBuilder app) => app.MapPost("/fake-google/token", Mint);

	private static IResult Mint(FakeTokenRequest? body, FakeGoogleIssuer issuer, ServerConfig config, TimeProvider clock)
	{
		if (string.IsNullOrEmpty(body?.Sub)) return Results.BadRequest(new { error = "sub is required." });

		var now = clock.GetUtcNow().UtcDateTime;
		var descriptor = new SecurityTokenDescriptor
		{
			Issuer = "https://accounts.google.com",
			Audience = config.GoogleClientId,
			IssuedAt = now,
			NotBefore = now,
			Expires = now + TimeSpan.FromHours(1),
			Claims = new Dictionary<string, object>
			{
				["sub"] = body.Sub,
				["email"] = body.Email ?? $"{body.Sub}@example.test",
				["email_verified"] = true,
			},
			SigningCredentials = new SigningCredentials(issuer.SigningKey, SecurityAlgorithms.RsaSha256),
		};
		return Results.Ok(new { idToken = new JsonWebTokenHandler().CreateToken(descriptor) });
	}
}
