namespace Zing.Core.Vault;

public sealed class PaddingException(string message) : Exception(message);

/// <summary>
/// ISO/IEC 7816-4 padding to the next multiple of 256 bytes, so a ciphertext's size says only roughly how big it is.
/// A 0x80 byte then zeros: unlike zero padding, content that ends in zeros comes back intact.
/// </summary>
public static class Padding
{
	public const int Block = 256;

	private const byte Marker = 0x80;

	public static byte[] Pad(ReadOnlySpan<byte> content)
	{
		var padded = new byte[(content.Length / Block + 1) * Block];
		content.CopyTo(padded);
		padded[content.Length] = Marker;
		return padded;
	}

	public static byte[] Unpad(ReadOnlySpan<byte> padded)
	{
		if (padded.Length == 0 || padded.Length % Block != 0) throw new PaddingException($"Padded length {padded.Length} isn't a positive multiple of {Block}");
		var marker = padded.LastIndexOfAnyExcept((byte)0);
		if (marker < 0 || padded[marker] != Marker) throw new PaddingException("No 0x80 marker before the zero padding");
		if (padded.Length - marker > Block) throw new PaddingException("Padding runs longer than one block");
		return padded[..marker].ToArray();
	}
}
