using DumpDebugger.TsGen;

// Usage: DumpDebugger.TsGen <out.ts> [--check]
// --check exits non-zero if the file on disk differs from what would be generated.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.TsGen <out.ts> [--check]");
    return 1;
}

var outPath = Path.GetFullPath(args[0]);
var generated = TsGenerator.Generate();

if (args.Contains("--check"))
{
    var current = File.Exists(outPath) ? File.ReadAllText(outPath).Replace("\r\n", "\n") : "";
    if (current != generated)
    {
        Console.Error.WriteLine($"{outPath} is out of date. Run `npm run gen:types` in web/.");
        return 2;
    }

    Console.WriteLine($"{outPath} is up to date.");
    return 0;
}

File.WriteAllText(outPath, generated);
Console.WriteLine($"Wrote {outPath}");
return 0;
