namespace Zing.Core.Net;

/// <summary>Something went wrong reaching or reading the network, with a message the user can be shown.</summary>
public abstract class NetError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What was typed is not a site name.</summary>
public sealed class InvalidHostError(string typed) : NetError(typed.Trim() == "" ? "Enter a site name, like example.com." : $"\"{typed.Trim()}\" isn't a site name. Enter one like example.com.");

/// <summary>The setting names a DoH provider Zing has no endpoint for.</summary>
public sealed class UnknownDohProviderError(string provider) : NetError($"\"{provider}\" isn't an encrypted DNS provider Zing knows.");

/// <summary>A public resolver answered, but not with an answer to use. The network may be blocking or rewriting encrypted DNS.</summary>
public abstract class ResolverFailedError(string message, Exception? inner = null) : NetError(message, inner);

/// <summary>The public resolver says the name does not exist, which is not a network's doing.</summary>
public sealed class NxDomainError(string host) : NetError($"{host} doesn't exist, even according to public DNS.");

/// <summary>The resolver could not be reached or refused the request.</summary>
public sealed class ResolverUnreachableError(string provider, string reason, Exception? inner = null)
	: ResolverFailedError($"Couldn't reach {provider}'s encrypted DNS service ({reason}).", inner);

/// <summary>The resolver answered with a DNS status other than success or NXDOMAIN.</summary>
public sealed class ResolverStatusError(int status) : ResolverFailedError($"The DNS resolver answered with status {status}.")
{
	public int Status { get; } = status;
}

/// <summary>The resolver's answer was not the JSON a DoH answer should be.</summary>
public sealed class MalformedDoHResponseError(string reason) : ResolverFailedError($"The DNS resolver sent an answer Zing couldn't read: {reason}.");
