using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DiagKit.Setup;

/// <summary>HTTP with the settings every download needs: modern TLS, a plain user agent, sensible timeouts.</summary>
public static class Net
{
    // A non-browser user agent makes SourceForge send the file instead of its "your download will start" page.
    public const string UserAgent = "DiagKit-setup/1.1";

    private static readonly HttpClient Client;
    private static readonly HttpClient NoRedirect;

    static Net()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        ServicePointManager.DefaultConnectionLimit = 16;
        Client = Create(allowRedirect: true);
        NoRedirect = Create(allowRedirect: false);
    }

    private static HttpClient Create(bool allowRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirect,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(60));
        using HttpResponseMessage r = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public static async Task<string> PostFormAsync(string url, IDictionary<string, string> form, CancellationToken ct)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(60));
        using HttpResponseMessage r = await Client.PostAsync(url, new FormUrlEncodedContent(form), cts.Token).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>Posts a form and returns where the server redirects to, without following it.</summary>
    public static async Task<string?> PostForRedirectAsync(string url, IDictionary<string, string> form, CancellationToken ct)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(30));
        using HttpResponseMessage r = await NoRedirect.PostAsync(url, new FormUrlEncodedContent(form), cts.Token).ConfigureAwait(false);
        Uri? location = r.Headers.Location;
        if (location == null)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(new Uri(url), location).AbsoluteUri;
    }

    /// <summary>ETag/Last-Modified of a link, to tell whether an unversioned file changed. Null when the server gives neither.</summary>
    public static async Task<string?> RemoteStampAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = Linked(ct, TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage r = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                return null;
            }

            string etag = r.Headers.ETag?.ToString() ?? "";
            string modified = r.Content.Headers.LastModified?.ToString("r") ?? "";
            return etag.Length > 0 || modified.Length > 0 ? "url:" + etag + "|" + modified : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Downloads to a file. A blocked or moved download often comes back as a web page with status 200; that counts as a failure.</summary>
    public static async Task DownloadAsync(string url, string path, CancellationToken ct, IProgress<long>? progress = null)
    {
        using var cts = Linked(ct, TimeSpan.FromMinutes(10));
        using HttpResponseMessage r = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)r.StatusCode} {r.ReasonPhrase}");
        }

        using (Stream body = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, cts.Token).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }

        using var check = File.OpenRead(path);
        var head = new byte[16];
        int n = check.Read(head, 0, head.Length);
        if (Encoding.ASCII.GetString(head, 0, n).TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            throw new InvalidDataException("the server returned a web page instead of the file");
        }
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    public static string Host(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    /// <summary>A short reason for a failed request, without .NET's stack of wrapper messages.</summary>
    public static string Describe(Exception ex)
    {
        Exception e = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
        if (e is TaskCanceledException)
        {
            return "timed out";
        }

        while (e.InnerException != null && e is HttpRequestException)
        {
            e = e.InnerException;
        }

        return e.Message.TrimEnd('.');
    }
}

/// <summary>Minimal JSON reading for manifests and API answers, using the serializer built into .NET Framework.</summary>
public static class Json
{
    private static readonly JavaScriptSerializer Serializer = new() { MaxJsonLength = int.MaxValue };

    public static object? Parse(string text) => Serializer.DeserializeObject(text);

    /// <summary>obj["a"]["b"], or null when any step is missing.</summary>
    public static object? Get(object? obj, params string[] path)
    {
        foreach (string key in path)
        {
            if (obj is IDictionary<string, object> d && d.TryGetValue(key, out object? next))
            {
                obj = next;
            }
            else
            {
                return null;
            }
        }

        return obj;
    }

    public static string Str(object? obj, params string[] path) => Get(obj, path) switch
    {
        string s => s,
        object[] arr when arr.Length > 0 => arr[0]?.ToString() ?? "",
        null => "",
        object o => o.ToString() ?? "",
    };

    public static IEnumerable<object> Array(object? obj, params string[] path)
        => Get(obj, path) is object[] arr ? arr : Enumerable.Empty<object>();
}

/// <summary>Hashes and Authenticode signatures.</summary>
public static class Verify
{
    /// <summary>Checks "sha256:ABC…", "sha1:…", "md5:…" or a bare SHA-256 against a file.</summary>
    public static bool HashMatches(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        string algorithm = "SHA256";
        string value = expected;
        int colon = expected.IndexOf(':');
        if (colon > 0)
        {
            algorithm = expected.Substring(0, colon).ToUpperInvariant();
            value = expected.Substring(colon + 1);
        }

        return string.Equals(Hash(path, algorithm), value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Hash(string path, string algorithm = "SHA256")
    {
        using HashAlgorithm h = algorithm switch
        {
            "SHA1" => SHA1.Create(),
            "MD5" => MD5.Create(),
            "SHA512" => SHA512.Create(),
            _ => SHA256.Create(),
        };
        using FileStream fs = File.OpenRead(path);
        return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "");
    }

    /// <summary>The signer's name when the file carries a valid Authenticode signature that chains to a trusted root; otherwise null.</summary>
    public static string? SignerName(string path, out string status)
    {
        int result = WinTrust.Verify(path);
        if (result != 0)
        {
            status = result switch
            {
                unchecked((int)0x800B0100) => "not signed",
                unchecked((int)0x80096010) => "signature does not match the file",
                unchecked((int)0x800B0109) => "untrusted root certificate",
                unchecked((int)0x800B0101) => "expired certificate",
                _ => "signature check failed (0x" + result.ToString("X8") + ")",
            };
            return null;
        }

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            status = "valid";
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return null;
        }
    }
}
