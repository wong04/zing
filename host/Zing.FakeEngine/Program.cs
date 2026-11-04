using System.Text.Json;

// Prints its arguments as one JSON array, so a test sees exactly what reached the process.
// `--fake-write <path>` also writes a small file there, standing in for an engine's output.
Console.Out.WriteLine(JsonSerializer.Serialize(args));

var write = Array.IndexOf(args, "--fake-write");
if (write < 0) return 0;
if (write + 1 >= args.Length)
{
	Console.Error.WriteLine("--fake-write needs a path");
	return 2;
}
File.WriteAllText(args[write + 1], "fake engine output\n");
return 0;
