using System.Text.Json.Nodes;

namespace Zing.Core.Tests;

/// <summary>"Never overwrite files" (preserveOriginals), read each time a tool puts a finished file in Downloads.</summary>
public sealed class OutputPlacementTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-placement-").FullName;
	private readonly SettingsStore _settings = new();
	private string Downloads => Path.Combine(_root, "downloads");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private string Made(string text)
	{
		var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
		File.WriteAllText(path, text);
		return path;
	}

	private string Existing(string name, string text)
	{
		Directory.CreateDirectory(Downloads);
		var path = Path.Combine(Downloads, name);
		File.WriteAllText(path, text);
		return path;
	}

	private string Place(string text, string name)
	{
		Directory.CreateDirectory(Downloads);
		return OutputPlacement.ForSettings(_settings).Place(Made(text), Downloads, name);
	}

	[Fact]
	public void With_the_setting_on_by_default_a_clashing_name_is_numbered_and_the_old_file_is_kept()
	{
		var old = Existing("cat.png", "mine");

		var placed = Place("new", "cat.png");

		Assert.Equal(Path.Combine(Downloads, "cat 2.png"), placed);
		Assert.Equal("mine", File.ReadAllText(old));
		Assert.Equal("new", File.ReadAllText(placed));
	}

	[Fact]
	public void With_the_setting_off_a_file_of_the_same_name_is_replaced()
	{
		_settings.Set("preserveOriginals", JsonValue.Create(false));
		var old = Existing("cat.png", "mine");

		var placed = Place("new", "cat.png");

		Assert.Equal(old, placed);
		Assert.Equal("new", File.ReadAllText(placed));
		Assert.Equal(["cat.png"], Directory.GetFiles(Downloads).Select(Path.GetFileName));
	}

	[Fact]
	public void The_setting_is_read_when_the_file_is_placed_not_when_the_placement_is_made()
	{
		var placement = OutputPlacement.ForSettings(_settings);
		Existing("cat.png", "mine");
		_settings.Set("preserveOriginals", JsonValue.Create(false));

		Assert.Equal(Path.Combine(Downloads, "cat.png"), placement.Place(Made("new"), Downloads, "cat.png"));
	}

	[Fact]
	public void With_the_setting_off_a_folder_of_that_name_is_not_replaced_and_the_file_is_numbered()
	{
		_settings.Set("preserveOriginals", JsonValue.Create(false));
		Directory.CreateDirectory(Path.Combine(Downloads, "cat.png"));

		Assert.Equal(Path.Combine(Downloads, "cat 2.png"), Place("new", "cat.png"));
	}

	[Fact]
	public void A_name_is_cleaned_the_same_way_in_both_states()
	{
		Assert.Equal(Path.Combine(Downloads, "ab.png"), Place("one", "a:b.png"));
		_settings.Set("preserveOriginals", JsonValue.Create(false));
		Assert.Equal(Path.Combine(Downloads, "ab.png"), Place("two", "a:b.png"));
	}
}
