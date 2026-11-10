using System.Diagnostics;
using System.Text;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>What a download gave: the text, or the reason there is none.</summary>
public sealed record FetchResult(string? Text, string? Failure, long Bytes, string? MediaType, TimeSpan Time);

/// <summary>
/// Downloads the text at an https address so it can be read as data. It follows no redirect, sends
/// no cookie and no credential, stops at a size cap and a time limit, and keeps nothing on disk.
/// </summary>
public static class DocumentFetch {
    public const int MaxBytes = 4 << 20;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // Read by the browser's handler and ignored everywhere else: the same two rules, in the only
    // form a browser accepts them.
    private static readonly HttpRequestOptionsKey<IDictionary<string, object>> BrowserOptions = new("WebAssemblyFetchOptions");

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient() {
        var handler = new HttpClientHandler();
        if (!OperatingSystem.IsBrowser()) {
            handler.AllowAutoRedirect = false;
            handler.UseCookies = false;
            handler.UseDefaultCredentials = false;
        }
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Whether the text is an address this will download. If not, <paramref name="refusal"/> says why.</summary>
    public static bool TryAddress(string? text, out Uri address, out string refusal) {
        address = null!;
        refusal = "";
        text = text?.Trim() ?? "";
        if (text.Length == 0) {
            refusal = "Type an https address to load.";
        } else if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0) {
            refusal = "That is not a full address. It should start with https://";
        } else if (parsed.Scheme != Uri.UriSchemeHttps) {
            refusal = $"Only https addresses are loaded, and this one is {parsed.Scheme}.";
        } else if (parsed.UserInfo.Length > 0) {
            refusal = "An address carrying a user name or password is not loaded.";
        } else {
            address = parsed;
            return true;
        }
        return false;
    }

    /// <summary>The host and the last part of the path: enough to say where a document came from.</summary>
    public static string Describe(Uri address) {
        var last = address.Segments.Length > 0 ? Uri.UnescapeDataString(address.Segments[^1]).Trim('/') : "";
        return last.Length == 0 ? address.Host : $"{address.Host}: {last}";
    }

    /// <summary>Downloads the text. Cancelling <paramref name="ct"/> throws; everything else is a result.</summary>
    public static async Task<FetchResult> GetTextAsync(Uri address, int maxBytes, TimeSpan timeout, CancellationToken ct) {
        var clock = Stopwatch.StartNew();
        FetchResult Fail(string why) { return new FetchResult(null, why, 0, null, clock.Elapsed); }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.5");
            request.Options.Set(BrowserOptions, new Dictionary<string, object> { ["redirect"] = "error", ["credentials"] = "omit" });

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) {
                var to = response.Headers.Location;
                var where = to is null ? "" : $" to {(to.IsAbsoluteUri ? to.Host : to.OriginalString)}";
                return Fail($"answered {status}, a redirect{where}, and redirects are not followed");
            }
            if (!response.IsSuccessStatusCode) { return Fail($"answered {status} {response.ReasonPhrase}".TrimEnd()); }
            if (response.Content.Headers.ContentLength is { } length && length > maxBytes) {
                return Fail($"says it is {length:N0} bytes, over the limit of {maxBytes:N0}, so it was not downloaded");
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[1 << 15];
            int read;
            while ((read = await body.ReadAsync(buffer, limit.Token)) > 0) {
                if (bytes.Length + read > maxBytes) { return Fail($"is over the limit of {maxBytes:N0} bytes, so the download was stopped"); }
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new FetchResult(reader.ReadToEnd(), null, bytes.Length, response.Content.Headers.ContentType?.MediaType, clock.Elapsed);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return Fail($"did not finish within {timeout.TotalSeconds:0.###} seconds, so the download was stopped");
        } catch (HttpRequestException ex) {
            return Fail("could not be fetched: " + ex.Message);
        } catch (IOException ex) {
            return Fail("stopped part way: " + ex.Message);
        }
    }
}
