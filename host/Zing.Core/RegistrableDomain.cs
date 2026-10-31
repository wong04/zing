using System.Globalization;

namespace Zing.Core;

/// <summary>src/shared/browser/registrableDomain.ts: one definition of "the same site" for tab grouping, favicons and phishing checks.</summary>
public static class RegistrableDomain
{
	private static readonly HashSet<string> TwoPartSuffixes = new(StringComparer.Ordinal)
	{
		"co.uk", "ac.uk", "gov.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "nhs.uk",
		"co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "co.nz", "org.nz", "govt.nz",
		"ac.nz", "co.za", "org.za", "gov.za", "com.au", "net.au", "org.au", "edu.au",
		"gov.au", "com.br", "net.br", "org.br", "gov.br", "com.cn", "net.cn", "org.cn",
		"gov.cn", "edu.cn", "com.mx", "org.mx", "gob.mx", "co.in", "net.in", "org.in",
		"gov.in", "ac.in", "co.kr", "or.kr", "go.kr", "ac.kr", "com.tr", "org.tr",
		"gov.tr", "edu.tr", "com.sg", "edu.sg", "gov.sg", "co.il", "org.il", "ac.il",
		"com.hk", "org.hk", "edu.hk", "gov.hk", "com.tw", "org.tw", "edu.tw", "gov.tw",
		"com.ar", "com.co", "com.pe", "com.ve", "com.ec", "co.id", "or.id", "ac.id",
		"go.id", "co.th", "or.th", "ac.th", "go.th", "in.th", "com.my", "org.my",
		"edu.my", "gov.my", "com.ph", "org.ph", "edu.ph", "gov.ph", "com.vn", "edu.vn",
		"gov.vn", "com.pk", "edu.pk", "gov.pk", "com.ng", "org.ng", "gov.ng", "co.ke",
		"or.ke", "ac.ke", "com.eg", "com.sa", "com.ua", "com.pl", "co.at", "or.at",
	};

	/// <summary>Swift's split(separator:) drops empty pieces.</summary>
	public static string[] LabelsOf(string host) => host.Split('.', StringSplitOptions.RemoveEmptyEntries);

	public static bool IsRawIPv4(string host)
	{
		var parts = LabelsOf(host);
		return parts.Length == 4 && parts.All(IsOctet);
	}

	/// <summary>The root domain a host belongs to: docs.google.com and mail.google.com are both google.com.</summary>
	public static string Of(string rawHost)
	{
		var host = rawHost.ToLowerInvariant();
		if (host.EndsWith('.')) host = host[..^1];
		// An address isn't a domain; cutting 192.168.1.10 to two labels would merge unrelated machines.
		if (IsRawIPv4(host) || host.Contains(':')) return host;
		var parts = LabelsOf(host);
		if (parts.Length <= 2) return host;
		var lastTwo = string.Join('.', parts[^2..]);
		return TwoPartSuffixes.Contains(lastTwo) ? string.Join('.', parts[^3..]) : lastTwo;
	}

	// Swift's Int(_:) accepts an optional sign and ASCII digits only.
	private static bool IsOctet(string part)
	{
		if (part.Length > 3) return false;
		var digits = part.Length > 0 && part[0] is '+' or '-' ? part[1..] : part;
		if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
		return int.Parse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) is >= 0 and <= 255;
	}
}
