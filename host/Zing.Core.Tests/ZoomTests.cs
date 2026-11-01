using Zing.Core;

namespace Zing.Core.Tests;

public class ZoomTests
{
	[Theory]
	[InlineData(1.0, 1.1)]
	[InlineData(0.5, 0.67)]
	[InlineData(2.5, 3.0)]
	[InlineData(3.0, 3.0)]
	// Ctrl+wheel can leave the engine between steps: the next step is the first one past it.
	[InlineData(1.2, 1.25)]
	public void In_goes_to_the_next_step(double from, double to) => Assert.Equal(to, Zoom.In(from));

	[Theory]
	[InlineData(1.0, 0.9)]
	[InlineData(0.67, 0.5)]
	[InlineData(0.5, 0.5)]
	[InlineData(1.2, 1.1)]
	// A factor a hair off a step (as the engine may report it) counts as that step.
	[InlineData(1.1000001, 1.0)]
	public void Out_goes_to_the_previous_step(double from, double to) => Assert.Equal(to, Zoom.Out(from));
}
