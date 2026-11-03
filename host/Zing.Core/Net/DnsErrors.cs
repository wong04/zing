namespace Zing.Core.Net;

/// <summary>A DNS server that is not an IP address of the family it was given for. It is not shown in a script, quoted or not.</summary>
public sealed class InvalidDnsAddressError(string value) : NetError($"\"{Shown(value)}\" isn't a DNS server address.")
{
	private static string Shown(string value) => value.Length > 60 ? $"{value[..60]}…" : value;
}

/// <summary>A DoH template that is not an https address.</summary>
public sealed class InvalidDohTemplateError(string value) : NetError($"\"{(value.Length > 80 ? $"{value[..80]}…" : value)}\" isn't an https address for encrypted DNS.");

public sealed class NoDnsAdaptersError : NetError
{
	public NoDnsAdaptersError() : base("No connected network adapter was found to change. Connect to a network (Ethernet or Wi-Fi) and try again.")
	{
	}
}

public sealed class DnsScriptTooLongError(int length) : NetError($"The script is too long to hand to Windows in one command ({length} characters), so it wasn't run.");

/// <summary>The script that lists the adapters printed something this version of Zing can't read.</summary>
public sealed class DnsListingError(string reason, Exception? inner = null) : NetError($"Couldn't read this PC's network adapters: {reason}", inner);

/// <summary>The script ran with administrator rights and failed. It may have changed some adapters before it did.</summary>
public sealed class DnsScriptFailedError(int exitCode, bool undoKept)
	: NetError($"The script failed (exit code {exitCode}).{(undoKept ? " Some of your settings may have changed; Undo puts the previous ones back." : "")}")
{
	public int ExitCode { get; } = exitCode;
}

/// <summary>The script found this Windows has no encrypted DNS support. It had changed nothing.</summary>
public sealed class NoEncryptedDnsError : NetError
{
	public NoEncryptedDnsError() : base("This version of Windows can't do encrypted DNS (it needs Windows 11), so nothing was changed.")
	{
	}
}

/// <summary>Apply was asked while the settings from an earlier Apply are still saved, which a second backup would overwrite with the encrypted ones.</summary>
public sealed class DnsAlreadyAppliedError : NetError
{
	public DnsAlreadyAppliedError() : base("Encrypted DNS was already set up by Zing. Undo it first.")
	{
	}
}

public sealed class NoDnsBackupError : NetError
{
	public NoDnsBackupError() : base("There are no saved settings to go back to: Zing hasn't changed this PC's DNS.")
	{
	}
}

/// <summary>The saved previous settings can't be read, so Undo would be guessing. The file is left as it is.</summary>
public sealed class DnsBackupCorruptError(string path, string reason, Exception? inner = null)
	: NetError($"The saved DNS settings in {path} can't be read ({reason}). Zing won't guess what to put back. Set DNS by hand in Windows Settings, then delete that file.", inner);
