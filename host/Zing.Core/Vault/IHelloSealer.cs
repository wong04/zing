using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Vault;

/// <summary>The person dismissed the Windows Hello prompt, or it timed out. Nothing was read or changed.</summary>
public sealed class HelloCancelledException(string message) : Exception(message);

/// <summary>Windows Hello isn't set up on this PC, or can't be used now. No Hello means no vault: there's no password-only fallback.</summary>
public sealed class HelloUnavailableException(string message) : Exception(message);

/// <summary>A Hello-sealed key in a format this Zing doesn't know.</summary>
public sealed class HelloSealFormatException(string message) : Exception(message);

/// <summary>
/// Seals the vault's secret key so only a Windows Hello verification opens it again (check C1's method in Zing.Host's HelloSealer).
/// Every call may show the Hello prompt; the e2e host and the tests swap in a fake that never does.
/// </summary>
public interface IHelloSealer
{
	/// <summary>Hello is set up and usable on this PC. Known without a prompt; false until the first check has finished.</summary>
	bool Available { get; }

	/// <summary>Fires when <see cref="Available"/> changes, so the renderer's vault state can follow.</summary>
	event Action? AvailabilityChanged;

	/// <exception cref="HelloCancelledException">The prompt was dismissed.</exception>
	/// <exception cref="HelloUnavailableException">Hello can't be used.</exception>
	Task<byte[]> SealAsync(byte[] key);

	/// <summary>The caller wipes the returned key with <see cref="CryptographicOperations.ZeroMemory"/>.</summary>
	/// <exception cref="HelloCancelledException">The prompt was dismissed.</exception>
	/// <exception cref="HelloUnavailableException">Hello can't be used.</exception>
	Task<byte[]> UnsealAsync(byte[] sealedBytes);
}

/// <summary>
/// Check C1's technique, without the Windows calls: the Hello key's RSA signature over a fixed challenge is deterministic and only
/// Hello can make it, so HKDF turns it into a wrapping key for <see cref="KeyWrap"/>. The blob is the version byte, the 32-byte
/// challenge, then the wrapped key, so a later version can change the challenge and still open the old blob.
/// </summary>
public static class HelloKeySeal
{
	public const string CredentialName = "Zing vault";
	public const byte Version = 1;

	private const int ChallengeSize = 32;
	private const string WrapLabel = "secret";
	private static readonly byte[] HkdfInfo = "zing-hello-v1"u8.ToArray();

	/// <summary>Not a secret: what matters is that only Hello can sign it.</summary>
	public static byte[] Challenge => SHA256.HashData(Encoding.UTF8.GetBytes("zing-hello-challenge-v1"));

	public static byte[] Seal(byte[] signature, byte[] key)
	{
		var challenge = Challenge;
		var wrappingKey = WrappingKey(signature);
		try
		{
			return [Version, .. challenge, .. KeyWrap.Wrap(wrappingKey, key, WrapLabel)];
		}
		finally
		{
			CryptographicOperations.ZeroMemory(wrappingKey);
		}
	}

	/// <summary>What Hello must sign to open <paramref name="sealedBytes"/>.</summary>
	public static byte[] ChallengeOf(byte[] sealedBytes)
	{
		Check(sealedBytes);
		return sealedBytes[1..(1 + ChallengeSize)];
	}

	/// <summary>The caller wipes the returned key.</summary>
	public static byte[] Open(byte[] signature, byte[] sealedBytes)
	{
		Check(sealedBytes);
		var wrappingKey = WrappingKey(signature);
		try
		{
			return KeyWrap.Unwrap(wrappingKey, sealedBytes[(1 + ChallengeSize)..], WrapLabel);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(wrappingKey);
		}
	}

	private static byte[] WrappingKey(byte[] signature) => RecoveryKey.Hkdf(signature, [], HkdfInfo, VaultCrypto.KeySize);

	private static void Check(byte[] sealedBytes)
	{
		if (sealedBytes.Length <= 1 + ChallengeSize) throw new HelloSealFormatException($"A Hello-sealed key is {sealedBytes.Length} bytes, too short for its header");
		if (sealedBytes[0] != Version) throw new HelloSealFormatException($"Hello seal version {sealedBytes[0]} isn't one this Zing knows (it knows {Version})");
	}
}
