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
//   (the file viewer's own commands) with "-movflags" it copies its "-i" file to the last argument in six pieces (an empty file is "invalid data"), saying how far it is on
//   stderr as FFmpeg does; with "-delete" it copies its first argument to the last. The input is a file the test made.
Console.Out.WriteLine(JsonSerializer.Serialize(args));
Console.Out.Flush();

if (args.Contains("-movflags")) return FakeViewerConversion.Video(args);
if (args.Contains("-delete")) return FakeViewerConversion.Image(args);

var code = 0;
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
return code;

static string Operand(string[] args, ref int i, string what)
{
	if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs {what}");
	return args[++i];
}

static class FakeViewerConversion
{
	private const int Pieces = 6;
	private const int PauseMilliseconds = 700;

	/// <summary>Writes the output while it is "converted", so a reader sees it grow and the progress lines come as FFmpeg's do.</summary>
	public static int Video(string[] args)
	{
		var input = File.ReadAllBytes(args[Array.IndexOf(args, "-i") + 1]);
		if (input.Length == 0)
		{
			Console.Error.WriteLine("Invalid data found when processing input");
			return 1;
		}
		Console.Error.WriteLine("  Duration: 00:00:06.00, start: 0.000000, bitrate: 100 kb/s");
		Console.Error.Flush();
		using var output = new FileStream(args[^1], FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
		for (var piece = 0; piece < Pieces; piece++)
		{
			var from = input.Length * piece / Pieces;
			var to = input.Length * (piece + 1) / Pieces;
			output.Write(input, from, to - from);
			output.Flush();
			Console.Error.WriteLine($"frame={(piece + 1) * 30,5} fps= 30 q=28.0 size={output.Length / 1024,7}kB time=00:00:0{piece + 1}.00 bitrate= 100.0kbits/s speed=1.0x");
			Console.Error.Flush();
			if (piece < Pieces - 1) Thread.Sleep(PauseMilliseconds);
		}
		return 0;
	}

	public static int Image(string[] args)
	{
		File.Copy(args[0], args[^1], overwrite: true);
		return 0;
	}
}
