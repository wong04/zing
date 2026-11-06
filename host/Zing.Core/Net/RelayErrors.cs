namespace Zing.Core.Net;

/// <summary>Opening a link failed in a way specific to getting a page, with a message the user can be shown.</summary>
public abstract class RelayError(string message, Exception? inner = null) : NetError(message, inner);

/// <summary>What was typed is not an address Zing can open. The message leaves out what was typed: it is nobody's business to keep.</summary>
public sealed class BadURLError() : RelayError("That isn't an address Zing can open. Enter one like example.com or https://example.com/page.");

/// <summary>The relay scheme stands for https, so an address of another scheme can't be opened in a relayed tab.</summary>
public sealed class RelayedAddressError() : RelayError("A relayed page needs an https address.");

/// <summary>The fetch ended at an address the relay scheme can't stand for (it maps https only), so the page can't be shown where it is.</summary>
public sealed class UnrelayableAddressError() : RelayError("The page moved to an address that isn't https, which a relayed page can't use.");

/// <summary>An address with a user name and password in it would hand them to a relay.</summary>
public sealed class CredentialsInURLError() : RelayError("That address has a user name or password in it, which Zing won't send to anyone. Remove it and try again.");

public sealed class HTTPStatusError(int status) : RelayError($"Server returned HTTP {status}.")
{
	public int Status { get; } = status;
}

public sealed class EmptyBodyError() : RelayError("The response came back empty.");

/// <summary>A pinned request connects to an address the lookup chose, which only makes sense for a certificate-checked HTTPS connection.</summary>
public sealed class NotHttpsError() : RelayError("This method needs an https address.");

public sealed class NoAddressError(string host) : RelayError($"Public DNS has no address for {host}.");

public sealed class TooManyRedirectsError() : RelayError("The site redirected too many times.");

/// <summary>The answer was a redirect the client declined to follow (to another scheme, or past its limit), so there is no page in it.</summary>
public sealed class RedirectNotFollowedError() : RelayError("The site redirected somewhere Zing wouldn't follow, so there was no page to show.");

public sealed class BodyTooLargeError() : RelayError("The page is over 16 MiB, more than Zing will read.");

/// <summary>A redirect that gives no place to go.</summary>
public sealed class BadRedirectError() : RelayError("The site redirected to somewhere Zing can't follow.");

/// <summary>The archive's lookup answered with something that isn't JSON.</summary>
public sealed class MalformedRelayAnswerError() : RelayError("The archive sent an answer Zing couldn't read.");

/// <summary>Every method was tried (or declined) and none produced a page.</summary>
public sealed class AllStrategiesFailedError(IReadOnlyList<RelayAttempt> attempts)
	: RelayError("Every method failed.\n\n" + string.Join("\n", attempts.Where(a => a.Status == AttemptStatus.Failed).Select(a => $"{RelayStrategies.Label(a.Strategy)}: {a.Reason}")))
{
	public IReadOnlyList<RelayAttempt> Attempts { get; } = attempts;
}

/// <summary>The whole run took longer than its deadline.</summary>
public sealed class RelayTimedOutError() : RelayError("Opening the link took too long and was stopped. Try again.");
