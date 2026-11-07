using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

public sealed class VaultCryptoTests
{
	private const string VectorsPath = "host/Zing.Core.Tests/Vault/vaultCrypto.vectors.json";

	private static JsonElement Vector(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	private static byte[] Hex(JsonElement v, string field) => Convert.FromHexString(v.GetProperty(field).GetString()!);

	private static readonly ItemAd Ad = new("user-1", "item-1", 7, ItemPart.Password);

	private static byte[] NewKey() => RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);

	[Fact]
	public void Argon2idMatchesTheKnownAnswer()
	{
		var v = Vector("argon2id");
		var kdf = new KdfParams(v.GetProperty("memoryKiB").GetInt64(), v.GetProperty("iterations").GetInt64(), v.GetProperty("parallelism").GetInt32(), Hex(v, "salt"));
		var output = Hex(v, "output");
		// Below the floor, so it calls the KDF directly instead of PasswordKey.Derive.
		Assert.Equal(output, PasswordKey.Argon2id(Hex(v, "password"), kdf, output.Length));
	}

	[Fact]
	public void HkdfMatchesTheKnownAnswer()
	{
		var v = Vector("hkdfSha256");
		Assert.Equal(Hex(v, "okm"), RecoveryKey.Hkdf(Hex(v, "ikm"), Hex(v, "salt"), Hex(v, "info"), v.GetProperty("length").GetInt32()));
	}

	[Fact]
	public void XChaCha20Poly1305MatchesTheKnownAnswer()
	{
		var v = Vector("xchacha20Poly1305");
		var expected = Hex(v, "nonce").Concat(Hex(v, "ciphertext")).Concat(Hex(v, "tag")).ToArray();
		Assert.Equal(expected, VaultCrypto.Encrypt(Hex(v, "key"), Hex(v, "nonce"), Hex(v, "plaintext"), Hex(v, "aad")));
		Assert.Equal(Hex(v, "plaintext"), VaultCrypto.Decrypt(Hex(v, "key"), expected, Hex(v, "aad")));
	}

	[Theory]
	[InlineData(0, 256)]
	[InlineData(1, 256)]
	[InlineData(255, 256)]
	[InlineData(256, 512)]
	[InlineData(257, 512)]
	public void PaddingRoundsUpToTheNextMultipleOf256(int length, int padded)
	{
		var content = RandomNumberGenerator.GetBytes(length);
		var result = Padding.Pad(content);
		Assert.Equal(padded, result.Length);
		Assert.Equal(content, Padding.Unpad(result));
	}

	[Fact]
	public void PaddingKeepsContentThatEndsLikePadding()
	{
		byte[] content = [1, 0x80, 0, 0];
		Assert.Equal(content, Padding.Unpad(Padding.Pad(content)));
	}

	public static TheoryData<byte[]> MalformedPadding()
	{
		var noMarker = new byte[256];
		var wrongMarker = new byte[256];
		wrongMarker[10] = 0x81;
		var twoBlocksOfPadding = new byte[512];
		twoBlocksOfPadding[0] = 0x80;
		var notABlock = new byte[255];
		notABlock[0] = 0x80;
		return new TheoryData<byte[]>([], noMarker, wrongMarker, twoBlocksOfPadding, notABlock);
	}

	[Theory]
	[MemberData(nameof(MalformedPadding))]
	public void UnpadRejectsMalformedPadding(byte[] padded)
	{
		Assert.Throws<PaddingException>(() => Padding.Unpad(padded));
	}

	[Fact]
	public void SealOpensWithTheSameKeyAndAd()
	{
		var key = NewKey();
		var plain = Encoding.UTF8.GetBytes("hunter2");
		var sealedBytes = VaultCrypto.Seal(key, plain, Ad);
		Assert.Equal(VaultCrypto.NonceSize + 256 + VaultCrypto.TagSize, sealedBytes.Length);
		Assert.Equal(plain, VaultCrypto.Open(key, sealedBytes, Ad));
	}

	[Fact]
	public void SealUsesAFreshNonceEachTime()
	{
		var key = NewKey();
		Assert.NotEqual(VaultCrypto.Seal(key, [1], Ad), VaultCrypto.Seal(key, [1], Ad));
	}

	public static TheoryData<ItemAd> ChangedAds() => new(
		Ad with { UserId = "user-2" },
		Ad with { ItemId = "item-2" },
		Ad with { Version = 8 },
		Ad with { Part = ItemPart.History });

	[Theory]
	[MemberData(nameof(ChangedAds))]
	public void OpenRejectsAnyChangeToTheAd(ItemAd changed)
	{
		var key = NewKey();
		var sealedBytes = VaultCrypto.Seal(key, [1, 2, 3], Ad);
		Assert.Throws<VaultTamperedException>(() => VaultCrypto.Open(key, sealedBytes, changed));
	}

	[Fact]
	public void OpenRejectsAChangedByteAWrongKeyAndATruncatedCiphertext()
	{
		var key = NewKey();
		var sealedBytes = VaultCrypto.Seal(key, [1, 2, 3], Ad);
		var flipped = (byte[])sealedBytes.Clone();
		flipped[VaultCrypto.NonceSize + 5] ^= 1;
		Assert.Throws<VaultTamperedException>(() => VaultCrypto.Open(key, flipped, Ad));
		Assert.Throws<VaultTamperedException>(() => VaultCrypto.Open(NewKey(), sealedBytes, Ad));
		Assert.Throws<VaultTamperedException>(() => VaultCrypto.Open(key, sealedBytes[..(VaultCrypto.NonceSize + VaultCrypto.TagSize - 1)], Ad));
	}

	[Fact]
	public void AdEncodingCantBeShiftedBetweenFields()
	{
		Assert.NotEqual(new ItemAd("ab", "c", 1, ItemPart.Summary).ToBytes(), new ItemAd("a", "bc", 1, ItemPart.Summary).ToBytes());
		Assert.NotEqual(new ItemAd("a|b", "c", 1, ItemPart.Summary).ToBytes(), new ItemAd("a", "b|c", 1, ItemPart.Summary).ToBytes());
	}

	[Fact]
	public void AdEncodingIsLengthPrefixedThenVersionThenPart()
	{
		byte[] expected = [0, 0, 0, 1, (byte)'u', 0, 0, 0, 2, (byte)'i', (byte)'d', 0, 0, 0, 0, 0, 0, 1, 2, 2];
		Assert.Equal(expected, new ItemAd("u", "id", 258, ItemPart.History).ToBytes());
	}

	public static TheoryData<KdfParams> WeakKdfs() => new(
		new KdfParams(64 * 1024, 3, 1, new byte[16]),
		new KdfParams(KdfParams.FloorMemoryKiB, 2, 1, new byte[16]),
		new KdfParams(KdfParams.FloorMemoryKiB, 3, 0, new byte[16]),
		new KdfParams(KdfParams.FloorMemoryKiB, 3, 1, new byte[8]));

	[Theory]
	[MemberData(nameof(WeakKdfs))]
	public void KdfParamsBelowTheFloorAreRefused(KdfParams kdf)
	{
		Assert.Throws<KdfTooWeakException>(kdf.EnforceFloor);
		Assert.Throws<KdfTooWeakException>(() => PasswordKey.Derive("correct horse battery staple", kdf));
	}

	[Fact]
	public void GeneratedKdfParamsAreAtTheFloor()
	{
		var kdf = KdfParams.Generate();
		kdf.EnforceFloor();
		Assert.Equal((262144L, 3L, 1), (kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism));
		Assert.Equal(16, kdf.Salt.Length);
	}

	[Fact]
	public void KeyWrapRejectsAWrongKeyOrLabel()
	{
		var wrappingKey = NewKey();
		var key = NewKey();
		var wrapped = KeyWrap.Wrap(wrappingKey, key, "index");
		Assert.Equal(key, KeyWrap.Unwrap(wrappingKey, wrapped, "index"));
		Assert.Throws<VaultTamperedException>(() => KeyWrap.Unwrap(wrappingKey, wrapped, "secret"));
		Assert.Throws<VaultTamperedException>(() => KeyWrap.Unwrap(NewKey(), wrapped, "index"));
	}

	// Runs Argon2id at the floor twice: the second derivation must unwrap what the first wrapped.
	[Fact]
	public void PasswordKeyWrapsAndUnwraps()
	{
		var kdf = KdfParams.Generate();
		var vaultKey = NewKey();
		var wrapped = KeyWrap.Wrap(PasswordKey.Derive("correct horse battery staple", kdf), vaultKey, "index");
		Assert.Equal(vaultKey, KeyWrap.Unwrap(PasswordKey.Derive("correct horse battery staple", kdf), wrapped, "index"));
	}

	// Runs Argon2id at the floor twice. "é" typed as one code point or as "e" plus a combining accent is the same password.
	[Fact]
	public void PasswordKeyNormalisesToNfc()
	{
		var kdf = KdfParams.Generate();
		Assert.Equal(PasswordKey.Derive("café au lait", kdf), PasswordKey.Derive("café au lait", kdf));
	}

	[Fact]
	public void RecoveryKeyIsTwentySixBase32CharactersInGroupsOfFour()
	{
		Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}-[A-Z2-7]{2}$", RecoveryKey.Generate());
	}

	// Worked by hand: RFC 4648 base32 of the bytes, then SHA-256's first two bits (10 here) in the last character.
	[Fact]
	public void RecoveryKeyFormatsKnownBytes()
	{
		Assert.Equal("AAAQ-EAYE-AUDA-OCAJ-BIFQ-YDIO-B6", RecoveryKey.Format([.. Enumerable.Range(0, 16).Select(i => (byte)i)]));
	}

	[Fact]
	public void RecoveryKeyWrapsAndUnwraps()
	{
		var code = RecoveryKey.Generate();
		var vaultKey = NewKey();
		var wrapped = KeyWrap.Wrap(RecoveryKey.ToKey(code), vaultKey, "secret");
		Assert.Equal(VaultCrypto.KeySize, RecoveryKey.ToKey(code).Length);
		Assert.Equal(vaultKey, KeyWrap.Unwrap(RecoveryKey.ToKey(code), wrapped, "secret"));
	}

	[Fact]
	public void RecoveryKeyAcceptsLowercaseAndSpaces()
	{
		const string code = "AAAQ-EAYE-AUDA-OCAJ-BIFQ-YDIO-B6";
		Assert.Equal(RecoveryKey.ToKey(code), RecoveryKey.ToKey("aaaq eaye auda ocaj bifq ydio b6"));
	}

	[Theory]
	[InlineData("AAAQ-CAYE-AUDA-OCAJ-BIFQ-YDIO-B6")] // second group mistyped; the checksum catches it
	[InlineData("AAAQ-EAYE-AUDA-OCAJ-BIFQ-YDIO-B")] // a character short
	[InlineData("AAAQ-EAYE-AUDA-OCAJ-BIFQ-YDIO-B6A")] // a character over
	[InlineData("AAAQ-EAYE-AUDA-OCAJ-BIFQ-YDIO-B1")] // 1 isn't in the alphabet
	[InlineData("")]
	public void RecoveryKeyRejectsAMistypedCode(string code)
	{
		Assert.Throws<RecoveryKeyFormatException>(() => RecoveryKey.ToKey(code));
	}
}
