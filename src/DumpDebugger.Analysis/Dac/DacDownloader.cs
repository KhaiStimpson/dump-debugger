using System.Runtime.InteropServices;

namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// Fetches one DAC from a symbol server into the app cache (§2.3 tier 4). Only constructed
/// after the user consents. Unlike ClrMD's built-in symbol server client it has a finite
/// timeout (headers and body), reports progress, writes atomically, and validates the image
/// before it enters the cache, so an interrupted or bad download never poisons later offline opens.
/// </summary>
public sealed class DacDownloader : IDisposable
{
    public static readonly Uri MicrosoftSymbolServer = new("https://msdl.microsoft.com/download/symbols/");
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    private const long MaxBytes = 128L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly TimeSpan _timeout;
    private string? _unreachable;

    public DacDownloader(Uri server, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _server = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
        _timeout = timeout;
        // The timeout is enforced per download below (it must cover the body too).
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Uri Server => _server;

    /// <summary>Downloads into the cache key path. Returns the cached path, or null if the server doesn't have it.</summary>
    /// <exception cref="TimeoutException">The server didn't deliver within the timeout.</exception>
    public async Task<string?> DownloadAsync(string cacheDirectory, string fileName, int timestamp, int imageSize,
        bool keyIsOwnIdentity, Architecture architecture, Action<long, long?>? progress, CancellationToken ct)
    {
        if (_unreachable is not null)
        {
            // One unreachable server shouldn't cost a timeout per candidate.
            throw new HttpRequestException(_unreachable);
        }

        var name = Path.GetFileName(fileName).ToLowerInvariant();
        var key = $"{name}/{unchecked((uint)timestamp):x8}{unchecked((uint)imageSize):x}/{name}";
        var target = DacCache.KeyPath(cacheDirectory, fileName, timestamp, imageSize);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.partial";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(_server, key), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Symbol server returned {(int)response.StatusCode} {response.ReasonPhrase} for {name}.", null, response.StatusCode);
            }

            var total = response.Content.Headers.ContentLength;
            if (total > MaxBytes)
            {
                throw new InvalidDataException($"Symbol server offered {total} bytes for {name}; refusing anything over {MaxBytes}.");
            }

            await using (var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long done = 0, reported = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxBytes)
                    {
                        throw new InvalidDataException($"Download of {name} exceeded {MaxBytes} bytes.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    if (done - reported >= 256 * 1024)
                    {
                        reported = done;
                        progress?.Invoke(done, total);
                    }
                }

                progress?.Invoke(done, total);
            }

            if (!DacImage.IsUsable(temp, architecture, keyIsOwnIdentity ? (timestamp, imageSize) : null))
            {
                throw new InvalidDataException($"The symbol server's {name} is not a complete {architecture} DAC matching this runtime; it was not cached.");
            }

            File.Move(temp, target, overwrite: true); // replaces a damaged entry, never leaves a partial one
            return target;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _unreachable = $"Timed out after {_timeout.TotalSeconds:0} s waiting for {_server.Host} to deliver {name}.";
            throw new TimeoutException(_unreachable);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            _unreachable = $"Could not reach {_server.Host}: {ex.Message}";
            throw new HttpRequestException(_unreachable, ex);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
