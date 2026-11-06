using Zing.Core;

namespace Zing.Core.Tests;

public sealed class EngineFocusTests
{
	[Fact]
	public void A_refused_move_is_tried_again()
	{
		var calls = 0;
		EngineFocus.Move(() =>
		{
			if (++calls == 1) throw new ArgumentException("Value does not fall within the expected range.");
		});
		Assert.Equal(2, calls);
	}

	[Fact]
	public void A_move_that_works_runs_once()
	{
		var calls = 0;
		EngineFocus.Move(() => calls++);
		Assert.Equal(1, calls);
	}

	[Fact]
	public void A_move_refused_twice_is_an_error()
	{
		var calls = 0;
		Assert.Throws<ArgumentException>(() => EngineFocus.Move(() =>
		{
			calls++;
			throw new ArgumentException("Value does not fall within the expected range.");
		}));
		Assert.Equal(2, calls);
	}

	[Fact]
	public void Any_other_failure_is_not_tried_again()
	{
		var calls = 0;
		Assert.Throws<InvalidOperationException>(() => EngineFocus.Move(() =>
		{
			calls++;
			throw new InvalidOperationException("The view has gone");
		}));
		Assert.Equal(1, calls);
	}
}
