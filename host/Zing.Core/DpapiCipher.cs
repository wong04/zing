using System.Security.Cryptography;

namespace Zing.Core;

/// <summary>Windows DPAPI for the current user: the sealed bytes open only for the same Windows user on the same machine.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiCipher : ISecretCipher
{
	public bool IsAvailable => true;

	public byte[] Encrypt(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

	public byte[] Decrypt(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser);
}
