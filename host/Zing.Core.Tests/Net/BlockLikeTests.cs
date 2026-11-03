using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class BlockLikeTests
{
	[Theory]
	[InlineData("HostNameNotResolved")]
	[InlineData("CannotConnect")]
	[InlineData("ServerUnreachable")]
	[InlineData("ConnectionReset")]
	[InlineData("Timeout")]
	[InlineData("CertificateCommonNameIsIncorrect")]
	[InlineData("CertificateExpired")]
	[InlineData("CertificateRevoked")]
	[InlineData("CertificateIsInvalid")]
	public void Names_the_failures_a_block_causes(string status)
	{
		Assert.True(BlockLike.Is(status));
	}

	[Theory]
	[InlineData("Unknown")]
	[InlineData("OperationCanceled")]
	[InlineData("ConnectionAborted")]
	[InlineData("Disconnected")]
	[InlineData("RedirectFailed")]
	[InlineData("ErrorHttpInvalidServerResponse")]
	[InlineData("ValidAuthenticationCredentialsRequired")]
	[InlineData("ClientCertificateContainsErrors")]
	[InlineData("UnexpectedError")]
	[InlineData("")]
	public void Leaves_every_other_status_alone(string status)
	{
		Assert.False(BlockLike.Is(status));
	}

	[Fact]
	public void An_unrecognised_name_is_not_a_block()
	{
		Assert.False(BlockLike.Is("hostnamenotresolved"));
		Assert.False(BlockLike.Is("ERR_CONNECTION_REFUSED"));
	}

	[Theory]
	[InlineData("https://blocked.test/page", "HostNameNotResolved", "https://blocked.test/page")]
	[InlineData("http://blocked.test/", "Timeout", "http://blocked.test/")]
	public void Offers_for_a_web_page_that_failed_like_a_block(string url, string status, string expected)
	{
		Assert.Equal(expected, LoadFailureOffer.For(url, status));
	}

	[Theory]
	[InlineData("zing://home", "HostNameNotResolved")]
	[InlineData("about:blank", "CannotConnect")]
	[InlineData("https://app.zing/index.html", "CannotConnect")]
	[InlineData("zing-relay://site.test/a", "HostNameNotResolved")]
	[InlineData("file:///c:/a.html", "CannotConnect")]
	[InlineData("https://blocked.test/", "OperationCanceled")]
	[InlineData("https://blocked.test/", "ErrorHttpInvalidServerResponse")]
	[InlineData("", "Timeout")]
	public void Makes_no_offer_for_internal_or_relayed_pages_or_other_failures(string url, string status)
	{
		Assert.Null(LoadFailureOffer.For(url, status));
	}
}
