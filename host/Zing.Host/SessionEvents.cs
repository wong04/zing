using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// Windows locked, or the session switched: every Zing window registers for WM_WTSSESSION_CHANGE (HostWindow) and reports it
/// here, on the UI thread. The vault wipes the secret key it holds on any of them (Ruling 44); a repeat from a second window
/// wipes nothing more. A window Windows won't register leaves Zing open, but unwatched for good: the failure is reported and
/// the vault stops holding the key, so Hello asks every time (Ruling 47).
/// </summary>
public sealed class SessionEvents : ISessionEvents
{
	public static SessionEvents Instance { get; } = new();

	private SessionEvents() { }

	public bool Watching { get; private set; } = true;

	public event Action? Switched;

	internal void Raise() => Switched?.Invoke();

	internal void Unwatched(Exception error)
	{
		Faults.Warning("session notification", error);
		Watching = false;
		Raise();
	}
}
