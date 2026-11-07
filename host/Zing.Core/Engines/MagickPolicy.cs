namespace Zing.Core.Engines;

/// <summary>
/// The policy.xml Zing writes for ImageMagick, found through MAGICK_CONFIGURE_PATH. It switches off the coders that read
/// other files, fetch addresses or run programs (MSL, MVG and the rest), every delegate, and "@" file lists. PDF, PS, EPS
/// and XPS go through Ghostscript and stay off until a tool that needs them says so. Resource limits stop a small file
/// that declares a huge canvas from filling the disk with its pixel cache; a photo of 16000 pixels a side still fits.
/// </summary>
internal static class MagickPolicy
{
	public const string FileName = "policy.xml";

	public const string Xml = """
		<?xml version="1.0" encoding="UTF-8"?>
		<policymap>
		  <policy domain="delegate" rights="none" pattern="*" />
		  <policy domain="coder" rights="none" pattern="{MSL,MVG,EPHEMERAL,URL,HTTP,HTTPS,FTP,TEXT,LABEL,CAPTION,PS,EPS,PDF,XPS}" />
		  <policy domain="path" rights="none" pattern="@*" />
		  <policy domain="filter" rights="none" pattern="*" />
		  <policy domain="resource" name="width" value="16KP" />
		  <policy domain="resource" name="height" value="16KP" />
		  <policy domain="resource" name="area" value="128MP" />
		  <policy domain="resource" name="memory" value="256MiB" />
		  <policy domain="resource" name="map" value="512MiB" />
		  <policy domain="resource" name="disk" value="1GiB" />
		  <policy domain="resource" name="time" value="300" />
		</policymap>
		""";
}
