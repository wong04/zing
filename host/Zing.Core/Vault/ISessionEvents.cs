namespace Zing.Core.Vault;

/// <summary>
/// Windows locked, or the session switched (another user, a remote disconnect, signing out): the host's WM_WTSSESSION_CHANGE.
/// The vault holds its secret key only while these are seen (Ruling 47).
/// </summary>
public interface ISessionEvents
{
	/// <summary>Locks and session switches are reported. False once Windows refused to report them to any window.</summary>
	bool Watching { get; }

	/// <summary>Windows locked or the session switched, or <see cref="Watching"/> became false.</summary>
	event Action? Switched;
}
