using System.Diagnostics;
using System.Text.Json;

// Prints its arguments as one JSON array, so a test sees exactly what reached the process, then does what
// the flags after them say, in order. Standing in for an engine, it never needs a real one.
//   --fake-write <path>       writes a small file there
//   --write-after <ms> <path> waits, then writes a small file there (a job cancelled in between must leave none)
//   --print-cwd               prints the working directory
//   --spawn-child             starts a copy of itself that sleeps, and prints "child <pid>"
//   --sleep <ms>              waits
//   --chatty <n>              prints n lines to stdout
//   --long-line <n>           prints one line of n characters
//   --err <text>              prints a line to stderr
//   --exit <code>             ends with that code
//   --print-env               prints "env" and the environment as one JSON object
//   --print-magick-policy     prints the policy.xml found through MAGICK_CONFIGURE_PATH, line by line
// Like a converter (Convert's e2e runs it as ffmpeg, magick and pandoc), when the last argument is a path that doesn't exist yet
// in a folder that does, it writes a small file there; and an input file (any argument that is a small existing file) whose first
// line is "fake-fail: <text>" makes it print <text> to stderr and exit 1, as a converter that can't read its input does, and
// "fake-sleep: <ms>" makes it wait that long first (a long conversion a test can cancel).
Console.Out.WriteLine(JsonSerializer.Serialize(args));
Console.Out.Flush();

var code = 0;
var wrote = false;
foreach (var argument in args)
{
	if (Marker(argument, "fake-sleep:") is { } sleep) Thread.Sleep(int.Parse(sleep));
	if (Marker(argument, "fake-fail:") is { } failure)
	{
		Console.Error.WriteLine(failure);
		Console.Error.Flush();
		return 1;
	}
}

for (var i = 0; i < args.Length; i++)
{
	switch (args[i])
	{
		case "--fake-write":
			File.WriteAllText(Operand(args, ref i, "a path"), "fake engine output\n");
			break;
		case "--write-after":
			var wait = int.Parse(Operand(args, ref i, "milliseconds"));
			var target = Operand(args, ref i, "a path");
			Thread.Sleep(wait);
			File.WriteAllText(target, "fake engine output\n");
			break;
		case "--print-cwd":
			Console.Out.WriteLine(Directory.GetCurrentDirectory());
			Console.Out.Flush();
			break;
		case "--spawn-child":
			var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--sleep", "60000" } })!;
			Console.Out.WriteLine($"child {child.Id}");
			Console.Out.Flush();
			break;
		case "--sleep":
			Thread.Sleep(int.Parse(Operand(args, ref i, "milliseconds")));
			break;
		case "--chatty":
			var count = int.Parse(Operand(args, ref i, "a count"));
			for (var line = 0; line < count; line++) Console.Out.WriteLine($"line {line}");
			Console.Out.Flush();
			break;
		case "--long-line":
			Console.Out.WriteLine(new string('x', int.Parse(Operand(args, ref i, "a length"))));
			Console.Out.Flush();
			break;
		case "--err":
			Console.Error.WriteLine(Operand(args, ref i, "text"));
			Console.Error.Flush();
			break;
		case "--print-env":
			var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);
			Console.Out.WriteLine("env " + JsonSerializer.Serialize(environment));
			Console.Out.Flush();
			break;
		case "--print-magick-policy":
			foreach (var policyLine in File.ReadAllLines(Path.Combine(Environment.GetEnvironmentVariable("MAGICK_CONFIGURE_PATH")!, "policy.xml"))) Console.Out.WriteLine(policyLine);
			Console.Out.Flush();
			break;
		case "--exit":
			code = int.Parse(Operand(args, ref i, "a code"));
			break;
	}
}
if (!wrote && args.Length > 0 && Path.IsPathRooted(args[^1]) && !File.Exists(args[^1]) && Directory.Exists(Path.GetDirectoryName(args[^1])))
{
	File.WriteAllText(args[^1], "fake engine output\n");
}
return code;

static string? Marker(string argument, string marker)
{
	if (!Path.IsPathRooted(argument) || !File.Exists(argument) || new FileInfo(argument).Length > 4096) return null;
	var first = File.ReadLines(argument).FirstOrDefault();
	return first is not null && first.StartsWith(marker, StringComparison.Ordinal) ? first[marker.Length..].Trim() : null;
}

static string Operand(string[] args, ref int i, string what)
{
	if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs {what}");
	return args[++i];
}
