using Zing.Core.Engines;

namespace Zing.Core.Tests;

public sealed class PdfPickerKindTests
{
	[Fact]
	public void The_picture_kind_lists_what_a_session_reads_and_nothing_else()
	{
		Assert.Equal(["png", "jpg", "jpeg", "webp"], FileKinds.Extensions(["picture"]));
	}

	[Fact]
	public void The_picker_can_be_asked_for_pdfs_alone()
	{
		Assert.Equal(["pdf"], FileKinds.Extensions(["pdf"]));
	}
}
