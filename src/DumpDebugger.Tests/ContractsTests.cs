using DumpDebugger.TsGen;

namespace DumpDebugger.Tests;

public class ContractsTests
{
    [Fact]
    public void Generated_typescript_matches_core_contracts()
    {
        var onDisk = File.ReadAllText(Repo.Path("web", "src", "contracts.gen.ts")).Replace("\r\n", "\n");
        Assert.True(
            onDisk == TsGenerator.Generate(),
            "web/src/contracts.gen.ts is stale. Run `npm run gen:types` in web/.");
    }
}
