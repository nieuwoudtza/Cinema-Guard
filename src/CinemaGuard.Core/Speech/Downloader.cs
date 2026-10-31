using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CinemaGuard.Core.Logging;
using ICSharpCode.SharpZipLib.BZip2;

namespace CinemaGuard.Core.Speech;

public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// HTTP downloads that survive interruptions: bytes go to "&lt;file&gt;.part", a later attempt continues
/// from where it stopped (HTTP Range), and the file only gets its real name once the size and
/// SHA-256 match. A half-downloaded file is therefore never mistaken for a finished one.
/// </summary>
public static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    /// <param name="progress">Called with the bytes of this file on disk so far (resumed bytes included).</param>
    public static async Task DownloadAsync(
        string url, string destination, long expectedSize, string? sha256, Action<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination) && (expectedSize <= 0 || new FileInfo(destination).Length == expectedSize))
        {
            progress?.Invoke(new FileInfo(destination).Length);
            return;
        }

        var partial = destination + ".part";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, partial, expectedSize, progress, cancellationToken);
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or IOException)
            {
                // A dropped connection: wait a moment and continue from the bytes already on disk.
                Log.Warn($"Download of {Path.GetFileName(destination)} interrupted ({ex.Message}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                throw new DownloadException($"Could not download {Path.GetFileName(destination)}: {ex.Message}", ex);
            }
        }

        var length = new FileInfo(partial).Length;
        if (expectedSize > 0 && length != expectedSize)
        {
            File.Delete(partial);
            throw new DownloadException($"{Path.GetFileName(destination)} downloaded as {length:N0} bytes, expected {expectedSize:N0}. Try again.");
        }

        if (!string.IsNullOrEmpty(sha256))
        {
            var actual = await HashAsync(partial, cancellationToken);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new DownloadException($"{Path.GetFileName(destination)} failed its checksum, so it was deleted. Try again.");
            }
        }

        File.Move(partial, destination, overwrite: true);
    }

    private static async Task DownloadOnceAsync(string url, string partial, long expectedSize, Action<long>? progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(partial);
            existing = 0;
        }

        if (expectedSize > 0 && existing == expectedSize)
        {
            progress?.Invoke(existing);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        HttpResponseMessage response;
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connecting.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("the server did not answer within 60 seconds");
            }
        }

        using var _ = response;

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server says we already have everything (or the part file is wrong): start over once.
            File.Delete(partial);
            throw new IOException("the partial download did not match the server's file");
        }

        if (!response.IsSuccessStatusCode)
            throw new DownloadException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase} for {url}");

        // 200 instead of 206 means the server ignored the range: rewrite from the start.
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existing = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        var written = existing;
        var lastReport = Environment.TickCount64;
        progress?.Invoke(written);

        // A connection that stops sending without closing would otherwise wait forever.
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (true)
        {
            int read;
            stalled.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                read = await source.ReadAsync(buffer, stalled.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("no data for 60 seconds");
            }

            if (read == 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;

            if (Environment.TickCount64 - lastReport >= 250)
            {
                lastReport = Environment.TickCount64;
                progress?.Invoke(written);
            }
        }

        progress?.Invoke(written);
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Unpacks the entries <paramref name="source"/> wants, flat, into <paramref name="folder"/>.</summary>
    public static int Extract(string archive, PackSource source, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var count = 0;

        if (source.Format == PackFormat.Zip)
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.Wants(entry.FullName)) continue;

                var target = SafeTarget(folder, source, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                count++;
            }
        }
        else if (source.Format == PackFormat.TarBz2)
        {
            using var file = File.OpenRead(archive);
            using var bzip = new BZip2InputStream(file);
            using var tar = new TarReader(bzip);

            while (tar.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                if (entry.DataStream is null || !source.Wants(entry.Name)) continue;

                var target = SafeTarget(folder, source, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                entry.DataStream.CopyTo(output);
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Entries under <see cref="PackSource.EntryPrefix"/> keep their sub-folders; picked names land flat.
    /// Either way the result must stay inside the folder (no "..\" escapes from a hostile archive).
    /// </summary>
    private static string SafeTarget(string folder, PackSource source, string entryPath)
    {
        var normalised = entryPath.Replace('\\', '/');
        var relative = source.EntryPrefix is not null && normalised.StartsWith(source.EntryPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalised[source.EntryPrefix.Length..]
            : normalised[(normalised.LastIndexOf('/') + 1)..];

        var root = Path.GetFullPath(folder);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException($"The archive entry {entryPath} points outside its folder.");

        return target;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CinemaGuard/{CoreInfo.Version}");
        return client;
    }
}
