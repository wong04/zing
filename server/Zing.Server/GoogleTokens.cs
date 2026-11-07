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

	// Asks the source to re-read its keys; the source decides how often it actually does.
	void RequestRefresh();
}

public sealed class GoogleKeySource : ISigningKeySource
{
	private const string Discovery = "https://accounts.google.com/.well-known/openid-configuration";

	private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration = new(
		Discovery,
		new OpenIdConnectConfigurationRetriever(),
		new HttpDocumentRetriever())
	{
		AutomaticRefreshInterval = TimeSpan.FromHours(1),
	};

	public async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken cancellation)
	{
		var document = await configuration.GetConfigurationAsync(cancellation);
		return document.SigningKeys.ToList();
	}

	public void RequestRefresh() => configuration.RequestRefresh();
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

	public void RequestRefresh()
	{
		Interlocked.Increment(ref refreshCount);
		lock (gate)
		{
			published.AddRange(pending);
			pending.Clear();
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
			keys.RequestRefresh();
			signingKeys = await keys.GetKeysAsync(cancellation);
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
