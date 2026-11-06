namespace Zing.Core;

public static class EngineFocus
{
	/// <summary>
	/// The engine's MoveFocus now and then returns E_INVALIDARG (an ArgumentException) for a live view and a
	/// valid reason (about 1 in 500 calls in a stress run, more with several Zings running), and takes the same call straight after. A second refusal
	/// is a real error and goes to the caller.
	/// </summary>
	public static void Move(Action moveFocus)
	{
		try
		{
			moveFocus();
		}
		catch (ArgumentException)
		{
			moveFocus();
		}
	}
}
