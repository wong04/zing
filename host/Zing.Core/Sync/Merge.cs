using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>
/// One copy of a login, as a merge sees it (Ruling 1): the summary decrypted with the index key, the password and each old password
/// still sealed. The summary carries the version each ciphertext was sealed under (Ruling 24), so a ciphertext moved as it is still opens.
/// </summary>
internal sealed record MergeInput(SummaryPayload Summary, byte[] Password, IReadOnlyList<byte[]> History);

/// <summary>
/// The field-by-field merge of two copies of a login against the last copy they had in common (spec, Sync design). Pure: nothing is
/// decrypted but the summaries, so it never needs Windows Hello. Source of truth for src/shared/vault/merge.vectors.json.
/// </summary>
internal static class Merge
{
	/// <summary>
	/// A field changed on one side only takes that side's value; changed on both, it takes the copy edited later (<c>updatedAt</c>;
	/// a tie goes to <paramref name="theirs"/>, the server's copy). The password with the later <c>passwordUpdatedAt</c> wins, and the
	/// other moves into the history as it is, so a merge never loses one. Every old password of either side is kept.
	/// </summary>
	/// <param name="common">The copy both started from; null when there is none, so every field that differs counts as changed on both.</param>
	public static MergeInput Items(MergeInput? common, MergeInput mine, MergeInput theirs)
	{
		var later = mine.Summary.UpdatedAt > theirs.Summary.UpdatedAt ? mine : theirs;

		T Field<T>(Func<SummaryPayload, T> field)
		{
			var m = field(mine.Summary);
			var t = field(theirs.Summary);
			if (EqualityComparer<T>.Default.Equals(m, t)) return m;
			if (common is not null)
			{
				var c = field(common.Summary);
				if (EqualityComparer<T>.Default.Equals(m, c)) return t;
				if (EqualityComparer<T>.Default.Equals(t, c)) return m;
			}
			return field(later.Summary);
		}

		var winner = PasswordWinner(common, mine, theirs);
		var other = ReferenceEquals(winner, mine) ? theirs : mine;
		var history = History(mine, theirs);
		if (!Same(other.Password, winner.Password) && !history.Any(h => Same(h.Sealed, other.Password)))
			history.Add((new HistoryPayload(other.Summary.PasswordVersion, winner.Summary.PasswordUpdatedAt), other.Password));
		var ordered = history
			.OrderByDescending(h => h.Entry.ReplacedAt)
			.ThenByDescending(h => h.Entry.Version)
			.ThenBy(h => Convert.ToHexString(h.Sealed), StringComparer.Ordinal)
			.ToList();

		var summary = new SummaryPayload(
			Field(s => s.Origin),
			Field(s => s.Username),
			Field(s => s.Title),
			Field(s => s.Note),
			Math.Min(mine.Summary.CreatedAt, theirs.Summary.CreatedAt),
			Math.Max(mine.Summary.UpdatedAt, theirs.Summary.UpdatedAt),
			winner.Summary.PasswordUpdatedAt,
			Field(s => s.DeletedAt),
			winner.Summary.PasswordVersion,
			[.. ordered.Select(h => h.Entry)],
			Field(s => s.NeverSave),
			Tombstone: false,
			Generation: Math.Max(mine.Summary.Generation, theirs.Summary.Generation));
		return new MergeInput(summary, winner.Password, [.. ordered.Select(h => h.Sealed)]);
	}

	/// <summary>The copy whose password is kept: the side that changed it, or of two that did, the later.</summary>
	private static MergeInput PasswordWinner(MergeInput? common, MergeInput mine, MergeInput theirs)
	{
		if (Same(mine.Password, theirs.Password)) return theirs;
		var mineChanged = common is null || !Same(mine.Password, common.Password);
		var theirsChanged = common is null || !Same(theirs.Password, common.Password);
		if (!mineChanged) return theirs;
		if (!theirsChanged) return mine;
		return mine.Summary.PasswordUpdatedAt > theirs.Summary.PasswordUpdatedAt ? mine : theirs;
	}

	/// <summary>Both sides' old passwords, each ciphertext once, with the earliest time it was replaced.</summary>
	private static List<(HistoryPayload Entry, byte[] Sealed)> History(MergeInput mine, MergeInput theirs)
	{
		var merged = new List<(HistoryPayload Entry, byte[] Sealed)>();
		foreach (var side in new[] { mine, theirs })
		{
			for (var i = 0; i < side.History.Count; i++)
			{
				var entry = side.Summary.History[i];
				var at = merged.FindIndex(h => Same(h.Sealed, side.History[i]));
				if (at < 0)
				{
					merged.Add((entry, side.History[i]));
					continue;
				}
				if (entry.ReplacedAt < merged[at].Entry.ReplacedAt) merged[at] = (merged[at].Entry with { ReplacedAt = entry.ReplacedAt }, merged[at].Sealed);
			}
		}
		return merged;
	}

	private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
