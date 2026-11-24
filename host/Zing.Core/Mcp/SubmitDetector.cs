using System.Text.RegularExpressions;

namespace Zing.Core.Mcp;

public enum AiAction { Click, Type, TypeSubmit, Select, Scroll, PressKey, Navigate, Download, FillLogin, OpenTab, CloseTab }

/// <summary>What the accessibility tree says about the element an action targets.</summary>
public sealed record AxNode(string Role, string Name, bool Editable, bool InForm, string? InputType);

/// <summary>Whether an action can't be taken back (sends, pays, deletes), judged from the target's role and accessible name.</summary>
public static class SubmitDetector
{
	public static readonly IReadOnlyList<string> Words =
		["pay", "buy", "purchase", "order", "checkout", "send", "post", "publish", "submit", "transfer", "delete", "confirm"];

	private static readonly Regex NameWords = new($@"\b(?:{string.Join('|', Words)})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	public static bool IsSubmit(AiAction action, AxNode target, string? key) => action switch
	{
		AiAction.TypeSubmit => true,
		// Enter sends in any editable, form or not (Gmail, Slack).
		AiAction.PressKey => target.Editable && string.Equals(key, "Enter", StringComparison.OrdinalIgnoreCase),
		AiAction.Click => IsSubmitControl(target),
		_ => false,
	};

	private static bool IsSubmitControl(AxNode target)
	{
		if (string.Equals(target.InputType, "submit", StringComparison.OrdinalIgnoreCase)) return true;
		return target.Role == "button" && NameWords.IsMatch(target.Name);
	}
}
