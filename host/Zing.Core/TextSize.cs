namespace Zing.Core;

/// <summary>Per-tab text size (src/shared/browser/textSize.ts): scales type only, unlike page zoom.</summary>
public static class TextSize
{
	public static readonly IReadOnlyList<int> Steps = [70, 80, 90, 100, 110, 125, 150, 175, 200];
	public const int Default = 100;

	public static int Bigger(int percent) => Steps[Math.Min(IndexOf(percent) + 1, Steps.Count - 1)];

	public static int Smaller(int percent) => Steps[Math.Max(IndexOf(percent) - 1, 0)];

	/// <summary>`!important` because pages set their own root font size; the override has to win.</summary>
	public static string Css(int percent) => $"html {{ font-size: {percent}% !important; }}";

	/// <summary>Puts the override in the page (or takes it out at the default), replacing any earlier one.</summary>
	public static string Script(int percent)
	{
		var css = percent == Default ? "" : Css(percent);
		return "(() => { const id = 'zing-text-size'; let style = document.getElementById(id); "
			+ "if (!style) { style = document.createElement('style'); style.id = id; document.documentElement.appendChild(style) } "
			+ $"style.textContent = {System.Text.Json.JsonSerializer.Serialize(css)} }})()";
	}

	private static int IndexOf(int percent)
	{
		var at = Steps.ToList().IndexOf(percent);
		return at == -1 ? Steps.ToList().IndexOf(Default) : at;
	}
}
