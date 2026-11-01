using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class LibraryStoreTests : IDisposable
{
	private static readonly JsonElement V = Vectors.Load("src/shared/library/library.vectors.json");

	private readonly List<string> _scratch = [];
	private long _time = 1000;
	private int _ids;
	private int _saves;

	private LibraryStore NewStore(BrowserLibrary? initial = null) =>
		new(initial ?? Library.EmptyLibrary(_time), _ => _saves++, () => _time, () => $"id-{++_ids}");

	private string ScratchFile()
	{
		var dir = Path.Combine(Path.GetTempPath(), "zing-lib-" + Guid.NewGuid());
		Directory.CreateDirectory(dir);
		_scratch.Add(dir);
		return Path.Combine(dir, "library.json");
	}

	public void Dispose()
	{
		foreach (var dir in _scratch) Directory.Delete(dir, true);
	}

	private static IEnumerable<string?> Strings(JsonElement list) => list.EnumerateArray().Select(i => i.GetString());

	public static TheoryData<string> StoreCases()
	{
		var data = new TheoryData<string>();
		foreach (var c in V.GetProperty("store").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(StoreCases))]
	public void Store_scenarios_from_the_shared_vectors(string name)
	{
		var c = V.GetProperty("store").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
		_time = c.GetProperty("now").GetInt64();
		var store = NewStore();
		foreach (var step in c.GetProperty("steps").EnumerateArray())
		{
			var expect = step.TryGetProperty("expect", out var e) ? e : default;
			string Arg(string key) => step.GetProperty(key).GetString()!;
			switch (Arg("op"))
			{
				case "at":
					_time = step.GetProperty("now").GetInt64();
					break;
				case "addFavourite":
					Assert.Equal(expect.GetString(), store.AddFavourite(Arg("title"), Arg("url")).ToString().ToLowerInvariant());
					break;
				case "addReading":
					Assert.Equal(expect.GetString(), store.AddReading(Arg("title"), Arg("url")).ToString().ToLowerInvariant());
					break;
				case "apply":
					Assert.Equal(expect.GetString(), store.Apply(LibraryStore.ParseLibraryAction(step.GetProperty("action"))));
					break;
				case "urlsOf":
					Assert.Equal(Strings(expect), store.UrlsOf(Arg("id")));
					break;
				case "urlOf":
					Assert.Equal(expect.GetString(), store.UrlOf(Arg("id")));
					break;
				case "isFolderNode":
					Assert.Equal(expect.GetBoolean(), store.IsFolderNode(Arg("id")));
					break;
				case "childrenOf":
					Assert.Equal(Strings(expect), store.ChildrenOf(Arg("id")).Select(n => n.Id));
					break;
				default:
					throw new InvalidOperationException($"unknown step {Arg("op")}");
			}
		}
		LibraryTests.AssertSame(c.GetProperty("expected"), store.Snapshot, name);
		Assert.Equal(c.GetProperty("saves").GetInt32(), _saves);
	}

	[Fact]
	public void Subscribers_hear_changes_until_they_unsubscribe()
	{
		var store = NewStore();
		var heard = new List<BrowserLibrary>();
		var unsubscribe = store.Subscribe(heard.Add);
		store.AddReading("One", "https://1.test/");
		Assert.Same(store.Snapshot, Assert.Single(heard));
		unsubscribe();
		store.AddReading("Two", "https://2.test/");
		Assert.Single(heard);
	}

	[Fact]
	public void Move_node_cases_from_the_shared_vectors()
	{
		foreach (var c in V.GetProperty("moveNode").EnumerateArray())
		{
			var nodes = c.GetProperty("nodes").Deserialize<List<BookmarkNode>>(ZingJson.Options)!;
			var moved = LibraryStore.MoveNode(nodes, c.GetProperty("id").GetString()!, c.GetProperty("to").GetInt32());
			var expected = c.GetProperty("expected");
			if (expected.ValueKind == JsonValueKind.Null)
			{
				Assert.Null(moved);
				continue;
			}
			LibraryTests.AssertSame(expected, moved);
		}
	}

	[Fact]
	public void Parse_library_cases_from_the_shared_vectors()
	{
		var vector = V.GetProperty("parseLibrary");
		var now = vector.GetProperty("now").GetInt64();
		foreach (var c in vector.GetProperty("valid").EnumerateArray())
			LibraryTests.AssertSame(c.GetProperty("expected"), LibraryStore.ParseLibrary(c.GetProperty("input"), now), c.GetProperty("name").GetString()!);
		foreach (var c in vector.GetProperty("invalid").EnumerateArray())
			Assert.Throws<InvalidDataException>(() => LibraryStore.ParseLibrary(c.GetProperty("input"), now));
	}

	[Fact]
	public void Library_actions_parse_from_the_shared_vectors()
	{
		var vector = V.GetProperty("parseLibraryAction");
		foreach (var c in vector.GetProperty("valid").EnumerateArray())
		{
			var expected = c.GetProperty("expected");
			string Str(string key) => expected.GetProperty(key).GetString()!;
			LibraryAction want = expected.GetProperty("type").GetString() switch
			{
				"rename" => new RenameAction(Str("id"), Str("title")),
				"remove" => new RemoveAction(Strings(expected.GetProperty("ids")).Select(s => s!).ToList()),
				"newFolder" => new NewFolderAction(Strings(expected.GetProperty("ids")).Select(s => s!).ToList()),
				"move" => new MoveAction(Str("id"), expected.GetProperty("to").GetInt32()),
				"setRead" => new SetReadAction(Str("id"), expected.GetProperty("read").GetBoolean()),
				var other => throw new InvalidOperationException($"unknown action type {other}"),
			};
			var got = LibraryStore.ParseLibraryAction(c.GetProperty("input"));
			// Records compare list fields by reference, so compare the wire shape instead.
			Assert.Equal(JsonSerializer.Serialize<object>(want, ZingJson.Options), JsonSerializer.Serialize<object>(got, ZingJson.Options));
			Assert.Equal(want.GetType(), got.GetType());
		}
		foreach (var bad in vector.GetProperty("invalid").EnumerateArray())
			Assert.Throws<InvalidLibraryActionException>(() => LibraryStore.ParseLibraryAction(bad));
	}

	[Fact]
	public void A_missing_library_file_is_an_empty_library()
	{
		var loaded = LibraryStore.LoadLibraryFile(ScratchFile(), 5);
		LibraryTests.AssertSame(V.GetProperty("emptyLibrary").GetProperty("expected"), loaded);
	}

	[Fact]
	public void A_saved_library_loads_back()
	{
		var path = ScratchFile();
		BrowserLibrary? saved = null;
		var store = new LibraryStore(Library.EmptyLibrary(1), l => saved = l, () => 1, () => "id-1");
		store.AddFavourite("A", "https://a.test/");
		File.WriteAllText(path, JsonSerializer.Serialize(saved, ZingJson.Options));
		var link = Assert.IsType<BookmarkLink>(Assert.Single(Library.Favourites(LibraryStore.LoadLibraryFile(path, 1))));
		Assert.Equal(("A", "https://a.test/"), (link.Title, link.Url));
	}

	[Fact]
	public void A_corrupt_file_is_refused_rather_than_started_empty_over()
	{
		var path = ScratchFile();
		File.WriteAllText(path, """{"bookmarks": [{"id": 1}], "readingList": []}""");
		Assert.Throws<LibraryFileException>(() => LibraryStore.LoadLibraryFile(path, 1));
		File.WriteAllText(path, "not json");
		Assert.Throws<LibraryFileException>(() => LibraryStore.LoadLibraryFile(path, 1));
	}

	[Fact]
	public void An_unreadable_path_is_an_error_not_an_empty_library()
	{
		var directoryAsFile = Path.GetDirectoryName(ScratchFile())!;
		Assert.Throws<LibraryFileException>(() => LibraryStore.LoadLibraryFile(directoryAsFile, 1));
	}

	private static readonly DateTime Stamp = new(2026, 3, 4, 5, 6, 7);

	[Fact]
	public void A_corrupt_library_is_kept_aside_and_Zing_starts_with_an_empty_one()
	{
		var path = ScratchFile();
		File.WriteAllText(path, "not json");

		var loaded = LibraryStore.LoadLibraryOrRecover(path, 5, Stamp);

		LibraryTests.AssertSame(V.GetProperty("emptyLibrary").GetProperty("expected"), loaded.Value);
		var problem = Assert.IsType<LoadProblem>(loaded.Problem);
		Assert.IsType<LibraryFileException>(problem.Error);
		Assert.Equal(path + ".corrupt-20260304-050607", problem.KeptAs);
		Assert.False(File.Exists(path));
		Assert.Equal("not json", File.ReadAllText(problem.KeptAs!));
	}

	[Fact]
	public void Opening_over_a_corrupt_library_saves_new_edits_to_the_usual_path()
	{
		var path = ScratchFile();
		File.WriteAllText(path, """{"bookmarks": [{"id": 1}], "readingList": []}""");
		var saved = new List<string>();

		var opened = LibraryStore.Open(path, p => { saved.Add(p); return _ => _saves++; }, () => 1, () => "id-1", Stamp);

		Assert.NotNull(opened.Problem!.KeptAs);
		Assert.Equal([path], saved);
		opened.Value.AddFavourite("A", "https://a.test/");
		Assert.Equal(1, _saves);
	}

	[Fact]
	public void An_unreadable_library_is_left_in_place_and_not_saved_over()
	{
		var path = Path.GetDirectoryName(ScratchFile())!;

		var opened = LibraryStore.Open(path, _ => _ => _saves++, () => 1, () => "id-1", Stamp);

		var problem = Assert.IsType<LoadProblem>(opened.Problem);
		Assert.True(problem.OriginalInPlace);
		Assert.True(Directory.Exists(path));
		opened.Value.AddFavourite("A", "https://a.test/");
		Assert.Equal(0, _saves);
		Assert.Single(Library.Favourites(opened.Value.Snapshot));
	}

	[Fact]
	public void A_good_library_opens_with_no_problem()
	{
		var opened = LibraryStore.Open(ScratchFile(), _ => _ => _saves++, () => 1, () => "id-1", Stamp);
		Assert.Null(opened.Problem);
	}
}
