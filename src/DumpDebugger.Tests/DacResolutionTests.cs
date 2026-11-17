using DumpDebugger.Analysis.Dac;

namespace DumpDebugger.Tests;

public class DacResolutionTests
{
    [Fact]
    public void Offline_lookup_reads_the_app_symbol_cache()
    {
        var name = $"test-dac-{Guid.NewGuid():N}.dll";
        var directory = Path.Combine(DacCache.GetCacheDirectory(), name, "123456781000");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "cached symbol payload");
        try
        {
            var options = DacResolver.BuildOptions(false);
            Assert.Empty(options.SymbolPaths);
            Assert.Equal(path, options.FileLocator!.FindPEImage(name, 0x12345678, 0x1000, false));
            Assert.Null(options.FileLocator.FindPEImage(name, 0x12345679, 0x1000, false));
            // A cached path is not sufficient when ClrMD requests PE identity validation.
            Assert.Null(options.FileLocator.FindPEImage(name, 0x12345678, 0x1000, true));
        }
        finally
        {
            Directory.Delete(Path.Combine(DacCache.GetCacheDirectory(), name), true);
        }
    }
}
