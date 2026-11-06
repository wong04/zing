using Zing.Core.Engines;

namespace Zing.Core.Tests;

public sealed class PdfPickerKindTests
{
	[Fact]
	public void The_picker_can_be_asked_for_pdfs_alone()
	{
		Assert.Equal(["pdf"], FileKinds.Extensions(["pdf"]));
	}
}
