namespace Zing.Core.Engines;

internal static class EngineVersions
{
	/// <summary>Compares dotted numbers part by part ("2026.10.01.10" is newer than "2026.10.01.9"); a missing part is 0. Null when either side isn't dotted numbers.</summary>
	public static int? Compare(string a, string b)
	{
		if (!TryParse(a, out var left) || !TryParse(b, out var right)) return null;
		for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
		{
			var l = i < left.Length ? left[i] : 0;
			var r = i < right.Length ? right[i] : 0;
			if (l != r) return l < r ? -1 : 1;
		}
		return 0;
	}

	private static bool TryParse(string version, out long[] parts)
	{
		var pieces = version.Split('.');
		parts = new long[pieces.Length];
		for (var i = 0; i < pieces.Length; i++)
		{
			if (!long.TryParse(pieces[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parts[i])) return false;
		}
		return true;
	}
}
