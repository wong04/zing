using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class DropRouterTests
{
	private const string VectorsPath = "src/shared/shell/dropRouter.vectors.json";

	private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-07-29T14:40:00Z");

	public static TheoryData<string> PlanNames() => Names("plans");

	public static TheoryData<string> DroppedNames() => Names("droppedNames");

	public static TheoryData<string> DownloadNames() => Names("downloadNames");

	public static TheoryData<string> SniffNames() => Names("sniff");

	public static TheoryData<string> UniqueNames() => Names("uniqueNames");

	public static TheoryData<string> ExtensionNames() => Names("extensions");

	public static TheoryData<string> FilePathNames() => Names("filePaths");

	public static TheoryData<string> WebUrlNames() => Names("webUrls");

	public static TheoryData<string> SafeNames() => Names("safeNames");

	public static TheoryData<string> TargetNames() => Names("targets");

	public static TheoryData<string> DragTypeNames() => Names("dragTypes");

	private static TheoryData<string> Names(string section)
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(string section, string name) =>
		Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

	private static byte[] Bytes(JsonElement b) =>
		[.. Convert.FromHexString(b.GetProperty("hex").GetString()!), .. new byte[b.GetProperty("zeros").GetInt32()]];

	private static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

	[Theory]
	[MemberData(nameof(PlanNames))]
	public void Shared_plan_vector(string name)
	{
		var c = Case("plans", name);
		var p = c.GetProperty("payload");
		var payload = new DropPayload(
			p.TryGetProperty("fileLocation", out var location) ? location.GetString() : null,
			p.TryGetProperty("image", out var image) ? Bytes(image) : null,
			p.TryGetProperty("url", out var url) ? url.GetString() : null,
			p.TryGetProperty("suggestedName", out var suggested) ? suggested.GetString() : null,
			p.TryGetProperty("otherTypes", out var other) ? other.EnumerateArray().Select(e => e.GetString()!).ToList() : null);
		var got = DropRouter.PlanDrop(payload, Now);
		var want = c.GetProperty("want");
		if (want.ValueKind == JsonValueKind.Null)
		{
			Assert.Null(got);
			return;
		}
		switch (want.GetProperty("kind").GetString())
		{
			case "open":
				Assert.Equal(new DropPlan.Open(want.GetProperty("path").GetString()!), got);
				break;
			case "download":
				Assert.Equal(new DropPlan.Download(want.GetProperty("url").GetString()!), got);
				break;
			case "write":
				var write = Assert.IsType<DropPlan.Write>(got);
				Assert.Equal(want.GetProperty("name").GetString(), write.Name);
				Assert.Equal(Bytes(want.GetProperty("bytes")), write.Bytes);
				break;
			case "materialise":
				var materialise = Assert.IsType<DropPlan.Materialise>(got);
				Assert.Equal(want.GetProperty("types").EnumerateArray().Select(e => e.GetString()!), materialise.Types);
				break;
			case var kind:
				throw new InvalidOperationException($"Unknown kind {kind}");
		}
	}

	[Theory]
	[MemberData(nameof(DroppedNames))]
	public void Shared_dropped_image_name_vector(string name)
	{
		var c = Case("droppedNames", name);
		Assert.Equal(c.GetProperty("want").GetString(), DropRouter.NameForDroppedImage(Bytes(c.GetProperty("bytes")), Text(c.GetProperty("suggestedName")), Now));
	}

	[Theory]
	[MemberData(nameof(DownloadNames))]
	public void Shared_download_name_vector(string name)
	{
		var c = Case("downloadNames", name);
		Assert.Equal(c.GetProperty("want").GetString(), DropRouter.NameForDownload(c.GetProperty("url").GetString()!, Text(c.GetProperty("mime")), Bytes(c.GetProperty("bytes"))));
	}

	[Theory]
	[MemberData(nameof(SniffNames))]
	public void Shared_sniff_vector(string name)
	{
		var c = Case("sniff", name);
		Assert.Equal(Text(c.GetProperty("want")), DropRouter.DetectExtension(Bytes(c.GetProperty("bytes"))));
	}

	[Theory]
	[MemberData(nameof(UniqueNames))]
	public void Shared_unique_name_vector(string name)
	{
		var c = Case("uniqueNames", name);
		var taken = c.GetProperty("taken").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
		Assert.Equal(c.GetProperty("want").GetString(), DropRouter.UniqueName(c.GetProperty("file").GetString()!, taken.Contains));
	}

	[Theory]
	[MemberData(nameof(ExtensionNames))]
	public void Shared_extension_vector(string name)
	{
		var c = Case("extensions", name);
		Assert.Equal(c.GetProperty("want").GetString(), DropRouter.PathExtension(c.GetProperty("file").GetString()!));
	}

	[Theory]
	[MemberData(nameof(FilePathNames))]
	public void Shared_file_path_vector(string name)
	{
		var c = Case("filePaths", name);
		Assert.Equal(Text(c.GetProperty("want")), DropRouter.AsFilePath(c.GetProperty("location").GetString()!));
	}

	[Theory]
	[MemberData(nameof(WebUrlNames))]
	public void Shared_web_url_vector(string name)
	{
		var c = Case("webUrls", name);
		Assert.Equal(Text(c.GetProperty("want")), DropRouter.AsWebUrl(c.GetProperty("item").GetString()!));
	}

	[Theory]
	[MemberData(nameof(SafeNames))]
	public void Shared_safe_name_vector(string name)
	{
		var c = Case("safeNames", name);
		Assert.Equal(c.GetProperty("want").GetString(), DropRouter.SafeFileName(c.GetProperty("file").GetString()!));
	}

	[Theory]
	[MemberData(nameof(TargetNames))]
	public void Shared_target_vector(string name)
	{
		var c = Case("targets", name);
		var content = PanelContent.FromId(c.GetProperty("content").GetString()!) ?? throw new InvalidOperationException("bad content");
		Assert.Equal(c.GetProperty("want").GetBoolean(), content.AcceptsFiles);
	}

	[Theory]
	[MemberData(nameof(DragTypeNames))]
	public void Shared_drag_type_vector(string name)
	{
		var c = Case("dragTypes", name);
		var types = c.GetProperty("types").EnumerateArray().Select(e => e.GetString()!).ToList();
		Assert.Equal(c.GetProperty("accepts").GetBoolean(), DropRouter.AcceptsDrag(types));
		Assert.Equal(c.GetProperty("notices").GetBoolean(), DropRouter.NoticesDrag(types));
	}

	[Fact]
	public void Hover_targets_stay_narrower_than_drop_targets()
	{
		Assert.All(DropRouter.HoverDragTypes, t => Assert.Contains(t, DropRouter.AcceptedDragTypes));
	}
}
