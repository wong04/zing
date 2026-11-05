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
//   --no-playlist -o <path> -- <url>   acts as yt-dlp (see FakeDownload below)
Console.Out.WriteLine(JsonSerializer.Serialize(args));
Console.Out.Flush();

// Standing in for yt-dlp (the Download tool's e2e): given "--no-playlist", "-o <path>" and an address after "--", it
// prints progress the way yt-dlp does, writes the file, and lets the address pick the ending.
//   ...bot-check... ends with YouTube's bot-check message   ...forbidden... ends with an HTTP 403
//   ...slow...      stops at 10% and waits to be cancelled
var outputFlag = Array.IndexOf(args, "-o");
var dashes = Array.IndexOf(args, "--");
if (Array.IndexOf(args, "--no-playlist") >= 0 && outputFlag >= 0 && outputFlag + 1 < args.Length && dashes >= 0 && dashes + 1 < args.Length)
{
	return FakeDownload(args[dashes + 1], args[outputFlag + 1], Array.IndexOf(args, "-x") >= 0);
}

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

static int FakeDownload(string url, string output, bool extractsAudio)
{
	if (url.Contains("bot-check"))
	{
		Console.Error.WriteLine("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.");
		return 1;
	}
	if (url.Contains("forbidden"))
	{
		Console.Error.WriteLine("ERROR: unable to download video data: HTTP Error 403: Forbidden");
		return 1;
	}
	foreach (var percent in new[] { 10, 45, 80, 100 })
	{
		Console.Out.WriteLine($"[download] {percent,3}.0% of 5.00MiB at 1.00MiB/s ETA 00:01");
		Console.Out.Flush();
		if (percent == 10 && url.Contains("slow")) Thread.Sleep(60_000);
		Thread.Sleep(150);
	}
	if (extractsAudio) Console.Out.WriteLine("[ExtractAudio] Destination: audio.mp3");
	Console.Out.Flush();
	File.WriteAllText(output, "fake media");
	return 0;
}

static string Operand(string[] args, ref int i, string what)
{
	if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs {what}");
	return args[++i];
}
