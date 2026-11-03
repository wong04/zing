using Zing.Core;

namespace Zing.Core.Tests;

public sealed class HashingTests : IDisposable
{
	private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-hash-").FullName;

	public void Dispose() => Directory.Delete(_dir, true);

	[Theory]
	[InlineData("MD5", "900150983cd24fb0d6963f7d28e17f72")]
	[InlineData("SHA-1", "a9993e364706816aba3e25717850c26c9cd0d89d")]
	[InlineData("SHA-256", Sha256OfAbc)]
	[InlineData("SHA-512", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
	public void Text_matches_the_published_vectors(string algorithm, string expected) =>
		Assert.Equal(expected, Hashing.Text("abc", algorithm));

	[Fact]
	public void Text_hashes_the_utf8_bytes()
	{
		var expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData([0xc3, 0xa9]));
		Assert.Equal(expected, Hashing.Text("é", "MD5"));
	}

	[Fact]
	public void Empty_text_has_a_digest()
	{
		Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hashing.Text("", "SHA-256"));
	}

	[Fact]
	public async Task File_hashes_to_the_same_digest_as_its_contents()
	{
		var path = Path.Combine(_dir, "abc.txt");
		await File.WriteAllTextAsync(path, "abc", TestContext.Current.CancellationToken);
		Assert.Equal(Sha256OfAbc, await Hashing.FileAsync(path, "SHA-256"));
	}

	[Fact]
	public async Task A_missing_file_is_an_error_not_an_empty_digest()
	{
		await Assert.ThrowsAsync<FileNotFoundException>(() => Hashing.FileAsync(Path.Combine(_dir, "nope.bin"), "MD5"));
	}

	[Fact]
	public void An_unknown_algorithm_is_refused()
	{
		var error = Assert.Throws<UnknownHashAlgorithmException>(() => Hashing.Text("abc", "CRC32"));
		Assert.Contains("CRC32", error.Message);
	}

	[Theory]
	[InlineData("MD5", "80070713463e7749b90c2dc24911e275")]
	[InlineData("SHA-1", "de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9")]
	[InlineData("SHA-256", "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")]
	[InlineData("SHA-512", "b42af09057bac1e2d41708e48a902e09b5ff7f12ab428a4fe86653c73dd248fb82f948a549f7b791a5b41915ee4d1ec3935357e4e2317250d0372afa2ebeeb3a")]
	public void Hmac_matches_the_published_vectors(string algorithm, string expected) =>
		Assert.Equal(expected, Hashing.Hmac("The quick brown fox jumps over the lazy dog", "key", algorithm));

	[Fact]
	public void Hmac_differs_by_key() =>
		Assert.NotEqual(Hashing.Hmac("m", "a", "SHA-256"), Hashing.Hmac("m", "b", "SHA-256"));
}
