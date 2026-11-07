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
}

public sealed class FakeGoogleIssuer : ISigningKeySource, IDisposable
{
	private readonly RSA rsa = RSA.Create(2048);

	public FakeGoogleIssuer()
	{
		SigningKey = new RsaSecurityKey(rsa) { KeyId = "fake-google-key" };
	}

	public SecurityKey SigningKey { get; }

	public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken cancellation) =>
		Task.FromResult<IReadOnlyCollection<SecurityKey>>([SigningKey]);

	public void Dispose() => rsa.Dispose();
}

public sealed class GoogleTokenValidator(ISigningKeySource keys, string clientId) : IGoogleTokenValidator
{
	private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];
	private readonly JsonWebTokenHandler handler = new();

	public async Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken cancellation)
	{
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
			IssuerSigningKeys = await keys.GetKeysAsync(cancellation),
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
}
