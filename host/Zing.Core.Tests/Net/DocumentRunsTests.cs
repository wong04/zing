using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class DocumentRunsTests
{
	[Fact]
	public void Leaving_the_document_cancels_what_it_started_and_not_what_comes_after()
	{
		using var runs = new DocumentRuns();
		var first = runs.Current;
		runs.LeaveDocument();
		Assert.True(first.IsCancellationRequested);
		Assert.False(runs.Current.IsCancellationRequested);
	}

	[Fact]
	public void Closing_cancels_the_current_document_and_leaves_nothing_to_start_with()
	{
		var runs = new DocumentRuns();
		var current = runs.Current;
		runs.Dispose();
		Assert.True(current.IsCancellationRequested);
		Assert.True(runs.Current.IsCancellationRequested);
		runs.LeaveDocument();
		Assert.True(runs.Current.IsCancellationRequested);
	}
}
