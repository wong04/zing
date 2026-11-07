using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Zing.Core.Vault;

/// <summary>Argon2id parameters weaker than the vault's floor, as a server trying a KDF downgrade would send.</summary>
public sealed class KdfTooWeakException(string message) : Exception(message);

/// <summary>Argon2id parameters, stored beside the wrapped keys. Memory is in KiB.</summary>
public sealed record KdfParams(long MemoryKiB, long Iterations, int Parallelism, byte[] Salt)
{
	public const long FloorMemoryKiB = 256 * 1024;
	public const long FloorIterations = 3;
	public const int SaltSize = 16;

	public static KdfParams Generate() => new(FloorMemoryKiB, FloorIterations, 1, RandomNumberGenerator.GetBytes(SaltSize));

	/// <summary>Parameters come back from the server, so they are checked before every use (the ETH Zurich KDF-downgrade attack).</summary>
	public void EnforceFloor()
	{
		if (MemoryKiB < FloorMemoryKiB) throw new KdfTooWeakException($"Argon2id memory {MemoryKiB} KiB is below the floor of {FloorMemoryKiB} KiB");
		if (Iterations < FloorIterations) throw new KdfTooWeakException($"Argon2id iterations {Iterations} are below the floor of {FloorIterations}");
		// libsodium, under NSec, runs one lane only.
		if (Parallelism != 1) throw new KdfTooWeakException($"Argon2id parallelism {Parallelism} isn't 1");
		if (Salt.Length != SaltSize) throw new KdfTooWeakException($"Argon2id salt is {Salt.Length} bytes, not {SaltSize}");
	}
}

/// <summary>The key derived from the Zing password. It only wraps the vault's keys; it never encrypts a login.</summary>
public static class PasswordKey
{
	public static byte[] Derive(string password, KdfParams kdf)
	{
		kdf.EnforceFloor();
		// The same password typed with a precomposed or a combining accent must give the same key.
		var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
		try
		{
			return Argon2id(bytes, kdf, VaultCrypto.KeySize);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	/// <summary>No floor check, so the known-answer test can run small parameters.</summary>
	internal static byte[] Argon2id(ReadOnlySpan<byte> password, KdfParams kdf, int length)
	{
		var parameters = new Argon2Parameters { DegreeOfParallelism = kdf.Parallelism, MemorySize = kdf.MemoryKiB, NumberOfPasses = kdf.Iterations };
		return PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(password, kdf.Salt, length);
	}
}
