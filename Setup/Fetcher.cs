using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace DiagKit.Setup;

/// <summary>What a source resolved to: the release, how to recognise it next time, and where to download it.</summary>
internal sealed class Resolved
{
    public string Label = "";
    public string Stamp = "";
    public string Hash = "";
    public string ExtractDir = "";
    public string FileName = "";
    public List<string> Urls = new();
    public string? TechPowerUpPage;
    public string? TechPowerUpId;
    public List<(string Name, string Url)>? Files;
}

public sealed class ToolResult
{
    public ToolResult(string state, string detail, bool ok, string? stamp = null)
    {
        State = state;
        Detail = detail;
        Ok = ok;
        Stamp = stamp;
    }

    /// <summary>installed, updated, up to date, FAILED.</summary>
    public string State { get; }
    public string Detail { get; }
    public bool Ok { get; }

    /// <summary>Stamp of the release now in the kit, for the ledger.</summary>
    public string? Stamp { get; }
}

/// <summary>
/// Downloads, verifies and installs one tool. Every source of a tool is tried in turn; a download is only installed
/// when it can be verified against the source's hash, the tool's Authenticode signer, or pinned file hashes.
/// Existing .ini and .cfg files are never overwritten, so tool settings survive updates.
/// </summary>
public sealed class Fetcher
{
    private static readonly string[] KeepExtensions = { ".ini", ".cfg" };
    private readonly string _work;
    private readonly SevenZip _sevenZip;

    public Fetcher(string workDir)
    {
        _work = workDir;
        _sevenZip = new SevenZip(workDir);
    }

    public bool Force { get; set; }

    /// <param name="dest">Tools\&lt;folder&gt;.</param>
    /// <param name="currentStamp">Stamp of the release already installed, or "".</param>
    public async Task<ToolResult> UpdateAsync(ToolSpec t, string dest, string currentStamp, Action<string> log, CancellationToken ct)
    {
        bool present = HasFiles(dest);
        var errors = new List<string>();

        for (int i = 0; i < t.Sources.Count; i++)
        {
            Source s = t.Sources[i];
            string sourceName = SourceName(s);
            bool more = i < t.Sources.Count - 1;
            try
            {
                Resolved src = await ResolveAsync(s, t, ct).ConfigureAwait(false);
                if (src.Hash.Length == 0 && t.Signer == null && t.FileHashes == null)
                {
                    throw new InvalidOperationException("this source has no hash and the tool has no signer or pinned hashes, so the download cannot be verified");
                }

                string label = src.Label;
                if (i > 0)
                {
                    // Name the fallback source in the summary; plain links already show their host while downloading.
                    label += s.Kind == "url" ? " (fallback link)" : " via " + sourceName;
                }

                bool upToDate = present && src.Stamp.Length > 0 && currentStamp == src.Stamp;
                if (!upToDate && !Force && src.Hash.Length > 0 && FindVerifiedCopy(dest, src.FileName, src.Hash) is { } copy)
                {
                    log($"up to date: {Path.GetFileName(copy)} matches the {src.Label} hash");
                    TidySkipped(t, dest);
                    return new ToolResult("up to date", label + " (verified local copy)", true, src.Stamp);
                }

                if (upToDate && !Force)
                {
                    TidySkipped(t, dest);
                    return new ToolResult("up to date", label, true, src.Stamp);
                }

                string stage = Path.Combine(_work, t.Folder + "-" + i);
                Directory.CreateDirectory(stage);
                string content;
                if (src.Files != null)
                {
                    // Loose files instead of an archive; the signature check below covers each one.
                    content = Path.Combine(stage, "files");
                    Directory.CreateDirectory(content);
                    log($"downloading {src.Files.Count} files from {Net.Host(src.Files[0].Url)}");
                    foreach ((string name, string url) in src.Files)
                    {
                        await SaveVerifiedAsync(new List<string> { url }, Path.Combine(content, name), "", log, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    string file = Path.Combine(stage, src.FileName);
                    if (src.TechPowerUpPage != null)
                    {
                        await SaveFromTechPowerUpAsync(src.TechPowerUpPage, src.TechPowerUpId!, file, src.Hash, label, log, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        List<string> urls = src.Urls;
                        if (urls.Count == 0)
                        {
                            throw new InvalidOperationException("no download link could be obtained");
                        }

                        log($"downloading {label} from {Net.Host(urls[0])}");
                        string used = await SaveVerifiedAsync(urls, file, src.Hash, log, ct).ConfigureAwait(false);
                        if (used != urls[0])
                        {
                            log("got it from " + Net.Host(used));
                        }
                    }

                    if (src.Hash.Length > 0)
                    {
                        log("hash verified");
                    }

                    content = await ExpandAsync(file, stage, src.ExtractDir, t.Pick, ct).ConfigureAwait(false);
                }

                if (t.Signer != null)
                {
                    AssertSigned(content, t.Signer);
                    log($"signature verified ({t.Signer})");
                }

                if (t.FileHashes != null)
                {
                    AssertFileHashes(content, t.FileHashes);
                    log("files match the pinned hashes");
                }

                await AddExtrasAsync(content, t.Extras, log, ct).ConfigureAwait(false);
                int copied = Merge(content, dest, t.Skip);
                TidySkipped(t, dest);
                string verb = present ? "updated" : "installed";
                log($"{verb} ({copied} files)");
                return new ToolResult(verb, label, true, src.Stamp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string msg = Net.Describe(ex);
                if (ex is IOException && msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new ToolResult("FAILED", "a file is in use. Close the tool and run setup again.", false);
                }

                errors.Add(sourceName + ": " + msg);
                if (more)
                {
                    log($"{sourceName} failed ({msg}), trying the next source");
                }
            }
        }

        string detail = string.Join(" | ", errors);
        if (t.Manual != null)
        {
            detail += " " + t.Manual;
        }

        return new ToolResult("FAILED", detail, false);
    }

    // ------------------------------------------------------------------ sources

    private static string SourceName(Source s) => s.Kind switch
    {
        "scoop" => "Scoop manifest " + s.Spec,
        "techpowerup" => "TechPowerUp download page",
        "sourceforge" => "SourceForge " + s.Project,
        "files" => Net.Host(s.Base),
        "github" => "GitHub release " + s.Repo,
        _ => Net.Host(s.Url),
    };

    private async Task<Resolved> ResolveAsync(Source s, ToolSpec t, CancellationToken ct)
    {
        switch (s.Kind)
        {
            case "techpowerup":
            {
                string page = $"https://www.techpowerup.com/download/{s.Page}/";
                string html = await Net.GetStringAsync(page, ct).ConfigureAwait(false);
                // Versions are listed newest first; each file entry carries its name, hashes and the form's file id.
                foreach (Match entry in Regex.Matches(html, @"(?s)<li class=""file[^""]*"">.*?</li>"))
                {
                    string file = Regex.Match(entry.Value, @"class=""filename""[^>]*>\s*([^<]+?)\s*<").Groups[1].Value;
                    Match m = Regex.Match(file, s.File);
                    if (!m.Success)
                    {
                        continue;
                    }

                    string sha = Regex.Match(entry.Value, @"(?s)SHA256:</div>\s*<div class=""hash-value"">([0-9A-Fa-f]{64})<").Groups[1].Value;
                    string id = Regex.Match(entry.Value, @"name=""id"" value=""(\d+)""").Groups[1].Value;
                    if (sha.Length == 0 || id.Length == 0)
                    {
                        throw new InvalidDataException($"the download page entry for {file} has no SHA-256 or file id");
                    }

                    string version = m.Groups["version"].Value;
                    return new Resolved
                    {
                        Label = "version " + version, Stamp = "techpowerup:" + version, Hash = "sha256:" + sha,
                        FileName = t.SaveAs ?? file, TechPowerUpPage = page, TechPowerUpId = id,
                    };
                }

                throw new InvalidDataException($"no file matching {s.File} on {page}");
            }

            case "scoop":
            {
                object? manifest = await ScoopManifestAsync(s.Spec, ct).ConfigureAwait(false);
                object? arch = Json.Get(manifest, "architecture", "64bit");
                string url = Json.Str(arch, "url");
                string hash = Json.Str(arch, "hash");
                string extractDir = Json.Str(arch, "extract_dir");
                if (url.Length == 0)
                {
                    url = Json.Str(manifest, "url");
                    hash = Json.Str(manifest, "hash");
                }

                if (extractDir.Length == 0)
                {
                    extractDir = Json.Str(manifest, "extract_dir");
                }

                if (url.Length == 0)
                {
                    throw new InvalidDataException("the Scoop manifest has no download URL");
                }

                string version = Json.Str(manifest, "version");
                (string direct, string name) = SplitDownloadUrl(url);
                return new Resolved
                {
                    Label = "version " + version, Stamp = "scoop:" + version, Hash = hash, ExtractDir = extractDir,
                    FileName = t.SaveAs ?? name, Urls = MirrorUrls(direct, s.Mirrors, s.AltUrls, version),
                };
            }

            case "sourceforge":
            {
                string feed = $"https://sourceforge.net/projects/{s.Project}/rss?path=/{s.Path}";
                var xml = new XmlDocument { XmlResolver = null };
                xml.LoadXml(await Net.GetStringAsync(feed, ct).ConfigureAwait(false));
                // The feed lists files newest first; titles are paths like /Windows_Portable/hwi_834.zip.
                foreach (XmlNode item in xml.SelectNodes("/rss/channel/item")!)
                {
                    string name = Path.GetFileName(item["title"]?.InnerText ?? "");
                    if (!Regex.IsMatch(name, s.Pattern))
                    {
                        continue;
                    }

                    string path = $"{s.Project}/{s.Path}/{name}";
                    return new Resolved
                    {
                        Label = "file " + name, Stamp = "sourceforge:" + name, FileName = name,
                        Urls = { "https://downloads.sourceforge.net/project/" + path, $"https://master.dl.sourceforge.net/project/{path}?viasf=1" },
                    };
                }

                throw new InvalidDataException($"no file matching {s.Pattern} in SourceForge's {s.Project}/{s.Path}");
            }

            case "files":
            {
                string first = s.Base + s.Files[0];
                string stamp = await Net.RemoteStampAsync(first, ct).ConfigureAwait(false)
                               ?? throw new InvalidDataException(Net.Host(first) + " did not answer");
                return new Resolved
                {
                    Label = "latest", Stamp = "files:" + stamp,
                    Files = s.Files.Select(f => (f, s.Base + f)).ToList(),
                };
            }

            case "github":
            {
                object? rel = Json.Parse(await Net.GetStringAsync($"https://api.github.com/repos/{s.Repo}/releases/latest", ct).ConfigureAwait(false));
                string tag = Json.Str(rel, "tag_name");
                object? asset = Json.Array(rel, "assets").FirstOrDefault(a => Regex.IsMatch(Json.Str(a, "name"), s.Asset))
                                ?? throw new InvalidDataException($"release {tag} has no file matching {s.Asset}");
                Match digest = Regex.Match(Json.Str(asset, "digest"), "^sha256:([0-9a-fA-F]{64})$");
                return new Resolved
                {
                    Label = "version " + tag, Stamp = "github:" + tag, Hash = digest.Success ? "sha256:" + digest.Groups[1].Value : "",
                    FileName = Json.Str(asset, "name"), Urls = { Json.Str(asset, "browser_download_url") },
                };
            }

            case "url":
            {
                (string direct, string name) = SplitDownloadUrl(s.Url);
                return new Resolved
                {
                    Label = "latest", Stamp = await Net.RemoteStampAsync(s.Url, ct).ConfigureAwait(false) ?? "",
                    FileName = name, Urls = { direct },
                };
            }

            default:
                throw new InvalidOperationException($"unknown source kind '{s.Kind}'");
        }
    }

    private static async Task<object?> ScoopManifestAsync(string spec, CancellationToken ct)
    {
        // raw.githubusercontent.com first; jsDelivr mirrors the same repositories if GitHub's raw host is down.
        string[] parts = spec.Split(new[] { '/' }, 2);
        string[] urls =
        {
            $"https://raw.githubusercontent.com/ScoopInstaller/{parts[0]}/master/bucket/{parts[1]}.json",
            $"https://cdn.jsdelivr.net/gh/ScoopInstaller/{parts[0]}@master/bucket/{parts[1]}.json",
        };
        var errors = new List<string>();
        foreach (string u in urls)
        {
            try
            {
                return Json.Parse(await Net.GetStringAsync(u, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(Net.Host(u) + ": " + Net.Describe(ex));
            }
        }

        throw new InvalidDataException($"could not read the Scoop manifest {spec}. " + string.Join("; ", errors));
    }

    /// <summary>Scoop's "url#/name" renames the download; sourceforge.net/projects/X/files/Y becomes the direct mirror redirect.</summary>
    internal static (string Url, string FileName) SplitDownloadUrl(string url)
    {
        string? name = null;
        int hash = url.IndexOf("#/", StringComparison.Ordinal);
        if (hash >= 0)
        {
            name = url.Substring(hash + 2);
            url = url.Substring(0, hash);
        }

        Match sf = Regex.Match(url, @"^https?://(?:www\.)?sourceforge\.net/projects?/([^/]+)/files/(.+?)(?:/download)?$");
        if (sf.Success)
        {
            url = $"https://downloads.sourceforge.net/project/{sf.Groups[1].Value}/{sf.Groups[2].Value}";
        }

        return (url, name ?? Path.GetFileName(new Uri(url).AbsolutePath));
    }

    /// <summary>
    /// Every place the same file can come from, in order: the link itself, alternative URLs, SourceForge's master host,
    /// mirrors, and finally the Wayback Machine's copy. Only used for hash-verified downloads, so any copy is as good as
    /// the original. Hosts already in the list are not repeated (http and https count as one).
    /// </summary>
    internal static List<string> MirrorUrls(string url, string[] mirrors, string[] altUrls, string version)
    {
        var list = new List<string> { url };
        foreach (string alt in altUrls)
        {
            int arrow = alt.IndexOf("=>", StringComparison.Ordinal);
            if (arrow > 0)
            {
                // "pattern=>replacement": the same file under another path on another host.
                var rx = new Regex(alt.Substring(0, arrow), RegexOptions.IgnoreCase);
                if (rx.IsMatch(url))
                {
                    list.Add(rx.Replace(url, alt.Substring(arrow + 2)));
                }
            }
            else
            {
                list.Add(alt.Replace("{version_}", version.Replace('.', '_')).Replace("{version}", version));
            }
        }
        Match sf = Regex.Match(url, @"^https?://downloads\.sourceforge\.net/project/(.+)$");
        if (sf.Success)
        {
            // Skips SourceForge's mirror-picking redirector, which is the part that usually fails.
            list.Add($"https://master.dl.sourceforge.net/project/{sf.Groups[1].Value}?viasf=1");
        }

        list.AddRange(mirrors.Select(m => m.TrimEnd('/') + new Uri(url).PathAndQuery));
        list.Add($"https://web.archive.org/web/{DateTime.Now:yyyyMMdd}id_/{url}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return list.Where(u =>
        {
            var uri = new Uri(u);
            return seen.Add(uri.Host + uri.PathAndQuery);
        }).ToList();
    }

    /// <summary>
    /// TechPowerUp: posting the file id returns the mirror list (closest first); posting id + server_id answers with a
    /// redirect to a short-lived link on that mirror. Links are asked for one mirror at a time, right before that
    /// mirror is tried: asking all of them up front cost one round trip each (and up to 30 s per slow mirror)
    /// before the download could even start, when the closest mirror nearly always works.
    /// </summary>
    private static async Task SaveFromTechPowerUpAsync(string page, string id, string path, string hash, string label, Action<string> log, CancellationToken ct)
    {
        const int MaxMirrors = 6;
        log("getting a download link from TechPowerUp");
        string html = await Net.PostFormAsync(page, new Dictionary<string, string> { ["id"] = id }, ct).ConfigureAwait(false);
        List<string> servers = Regex.Matches(html, @"name=""server_id"" value=""(\d+)""").Cast<Match>()
            .Select(m => m.Groups[1].Value).Distinct().Take(MaxMirrors).ToList();
        if (servers.Count == 0)
        {
            throw new InvalidDataException("the TechPowerUp download page listed no mirrors");
        }

        var errors = new List<string>();
        foreach (string server in servers)
        {
            string? link;
            try
            {
                link = await Net.PostForRedirectAsync(page, new Dictionary<string, string> { ["id"] = id, ["server_id"] = server }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"mirror {server}: {Net.Describe(ex)}");
                continue;
            }

            if (link == null)
            {
                errors.Add($"mirror {server}: no link");
                continue;
            }

            log($"downloading {label} from {Net.Host(link)}");
            try
            {
                await SaveVerifiedAsync(new List<string> { link }, path, hash, log, ct).ConfigureAwait(false);
                return;
            }
            catch (InvalidDataException ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(ex.Message);
                log($"{Net.Host(link)} failed, trying the next mirror");
            }
        }

        throw new InvalidDataException("no TechPowerUp mirror delivered the file. " + string.Join("; ", errors));
    }

    // ------------------------------------------------------------------ download and verify

    /// <summary>Tries each URL until one returns a real file that matches the hash. Returns the URL that worked.</summary>
    private static async Task<string> SaveVerifiedAsync(List<string> urls, string path, string hash, Action<string> log, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                await Net.DownloadAsync(url, path, ct).ConfigureAwait(false);
                if (hash.Length > 0 && !Verify.HashMatches(path, hash))
                {
                    throw new InvalidDataException("hash mismatch");
                }

                return url;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(Net.Host(url) + ": " + Net.Describe(ex));
                TryDelete(path);
                if (urls.Count > 1)
                {
                    log($"{Net.Host(url)} failed ({Net.Describe(ex)}), trying the next mirror");
                }
            }
        }

        throw new InvalidDataException("all download links failed. " + string.Join("; ", errors));
    }

    /// <summary>A single-file tool already in the kit counts as installed if it matches the release's hash.</summary>
    private static string? FindVerifiedCopy(string folder, string fileName, string hash)
    {
        string ext = Path.GetExtension(fileName);
        if (!Directory.Exists(folder) || ext is ".zip" or ".7z" or ".rar" || ext.Length == 0)
        {
            return null;
        }

        return Directory.GetFiles(folder, "*" + ext).FirstOrDefault(f => Verify.HashMatches(f, hash));
    }

    private async Task<string> ExpandAsync(string file, string stage, string extractDir, string? pick, CancellationToken ct)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        string raw = Path.Combine(stage, "raw");
        Directory.CreateDirectory(raw);
        switch (ext)
        {
            case ".zip":
                ZipFile.ExtractToDirectory(file, raw);
                break;
            case ".7z":
            case ".rar":
            case ".exe" when pick != null:
                await _sevenZip.ExtractAsync(file, raw, ct).ConfigureAwait(false);
                break;
            default:
                File.Copy(file, Path.Combine(raw, Path.GetFileName(file)));
                break;
        }

        string source = extractDir.Length > 0 ? Path.Combine(raw, extractDir) : raw;
        if (!Directory.Exists(source))
        {
            throw new InvalidDataException($"expected folder '{extractDir}' is missing from the download");
        }

        if (pick != null)
        {
            // Take one file out of a bigger package, preferring the 64-bit copy.
            string hit = Directory.GetFiles(source, pick, SearchOption.AllDirectories)
                             .OrderBy(f => Regex.IsMatch(f, @"[\\/]bin32[\\/]", RegexOptions.IgnoreCase))
                             .ThenBy(f => f.Length)
                             .FirstOrDefault()
                         ?? throw new InvalidDataException(pick + " was not found in the download");
            string picked = Path.Combine(stage, "picked");
            Directory.CreateDirectory(picked);
            File.Copy(hit, Path.Combine(picked, Path.GetFileName(hit)));
            return picked;
        }

        // Archives that wrap everything in a single top-level folder: use that folder's contents.
        string[] entries = Directory.GetFileSystemEntries(source);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : source;
    }

    private static IEnumerable<string> CodeFiles(string folder)
        => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll" or ".sys" or ".scr" or ".com");

    private static void AssertSigned(string folder, string signer)
    {
        foreach (string f in CodeFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll"))
        {
            string? name = Verify.SignerName(f, out string status);
            if (name != signer)
            {
                string got = name != null ? "signed by " + name : status;
                throw new InvalidDataException($"{Path.GetFileName(f)} is not validly signed by {signer} ({got}). Not installing it");
            }
        }
    }

    /// <summary>Every pinned file must be present and match, and no other executable code may come along with them.</summary>
    private static void AssertFileHashes(string folder, Dictionary<string, string[]> pinned)
    {
        foreach (KeyValuePair<string, string[]> p in pinned)
        {
            string path = Path.Combine(folder, p.Key);
            if (!File.Exists(path))
            {
                throw new InvalidDataException(p.Key + " is missing from the download");
            }

            string hash = Verify.Hash(path);
            if (!p.Value.Contains(hash, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{p.Key} does not match a known build (SHA-256 {hash}). If the author released a new version, add its hash to Setup\\Catalog.cs after checking it");
            }
        }

        foreach (string f in CodeFiles(folder))
        {
            string rel = f.Substring(folder.TrimEnd('\\').Length + 1);
            if (!pinned.Keys.Any(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(rel + " is not one of the pinned files. Not installing it");
            }
        }
    }

    /// <summary>Extras improve a tool but are not required, so a failure here is a warning, not a failed install.</summary>
    private static async Task AddExtrasAsync(string folder, List<Extra> extras, Action<string> log, CancellationToken ct)
    {
        foreach (Extra x in extras)
        {
            string target = Path.Combine(folder, x.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string file = string.Join("/", x.File.Split('/').Select(Uri.EscapeDataString));
            var urls = new List<string>
            {
                $"https://raw.githubusercontent.com/{x.Repo}/{x.Commit}/{file}",
                $"https://cdn.jsdelivr.net/gh/{x.Repo}@{x.Commit}/{file}",
            };
            try
            {
                await SaveVerifiedAsync(urls, target, x.Hash, log, ct).ConfigureAwait(false);
                log("fetched " + x.Path);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log($"could not add {x.Path}: {Net.Describe(ex)}");
            }
        }
    }

    /// <summary>Copies the verified files into the kit, keeping existing settings and leaving out skipped builds.</summary>
    private static int Merge(string from, string to, string[] skip)
    {
        int count = 0;
        string root = from.TrimEnd('\\');
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(root.Length + 1);
            if (IsSkipped(rel, skip))
            {
                continue;
            }

            string target = Path.Combine(to, rel);
            if (File.Exists(target) && KeepExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.Copy(f, target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>Removes 32-bit/ARM64 builds that kits set up before 1.1 still carry.</summary>
    private static void TidySkipped(ToolSpec t, string dest)
    {
        if (t.Skip.Length == 0 || !Directory.Exists(dest))
        {
            return;
        }

        string root = dest.TrimEnd('\\');
        foreach (string f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
        {
            if (IsSkipped(f.Substring(root.Length + 1), t.Skip))
            {
                TryDelete(f);
            }
        }
    }

    private static bool IsSkipped(string rel, string[] skip)
        => skip.Any(p => p.Contains('\\')
            ? Wildcard.IsMatch(p, rel)
            : Wildcard.IsMatch(p, Path.GetFileName(rel)) && !rel.Contains('\\'));

    public static bool HasFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) &&
                   Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => Path.GetFileName(f) != Kit.KitLayout.LegacyStamp);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // In use; leave it.
        }
    }
}

/// <summary>
/// 7-Zip, for the smartmontools installer and TestMem5's .rar. Uses an installed copy when there is one; otherwise
/// unpacks the official MSI into the work folder (an administrative extract: nothing is installed).
/// </summary>
internal sealed class SevenZip
{
    private readonly string _work;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _exe;

    public SevenZip(string work)
    {
        _work = work;
    }

    public async Task ExtractAsync(string archive, string target, CancellationToken ct)
    {
        string exe = await GetAsync(ct).ConfigureAwait(false);
        ProcessResult r = await RunAsync(exe, $"x \"{archive}\" -o\"{target}\" -y", ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidDataException($"7-Zip could not extract {Path.GetFileName(archive)} (exit code {r.ExitCode})");
        }
    }

    private async Task<string> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_exe != null)
            {
                return _exe;
            }

            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "7zip", "current", "7z.exe"),
            };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
                .Where(p => p.Trim().Length > 0).Select(p => Path.Combine(p.Trim(), "7z.exe")));
            _exe = candidates.FirstOrDefault(c =>
            {
                try
                {
                    return File.Exists(c);
                }
                catch
                {
                    return false;
                }
            });
            if (_exe != null)
            {
                return _exe;
            }

            // Main/7zip carries the MSI link and its hash.
            object? manifest = Json.Parse(await Net.GetStringAsync("https://raw.githubusercontent.com/ScoopInstaller/Main/master/bucket/7zip.json", ct).ConfigureAwait(false));
            string url = Json.Str(manifest, "architecture", "64bit", "url");
            string hash = Json.Str(manifest, "architecture", "64bit", "hash");
            (string direct, string name) = Fetcher.SplitDownloadUrl(url);
            string msi = Path.Combine(_work, name);
            await Net.DownloadAsync(direct, msi, ct).ConfigureAwait(false);
            if (!Verify.HashMatches(msi, hash))
            {
                throw new InvalidDataException("the 7-Zip download failed its hash check");
            }

            string target = Path.Combine(_work, "7zip");
            ProcessResult r = await RunAsync("msiexec.exe", $"/a \"{msi}\" /qn TARGETDIR=\"{target}\"", ct).ConfigureAwait(false);
            _exe = Directory.Exists(target) ? Directory.GetFiles(target, "7z.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (r.ExitCode != 0 || _exe == null)
            {
                throw new InvalidDataException($"could not unpack 7-Zip (msiexec exit code {r.ExitCode})");
            }

            return _exe;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using Process p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            while (!p.WaitForExit(250))
            {
                if (ct.IsCancellationRequested)
                {
                    try
                    {
                        p.Kill();
                    }
                    catch
                    {
                        // Already gone.
                    }

                    ct.ThrowIfCancellationRequested();
                }
            }

            p.WaitForExit();
            return new ProcessResult(p.ExitCode, stdout.Result + stderr.Result);
        }, ct);
    }
}

internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, string output)
    {
        ExitCode = exitCode;
        Output = output;
    }

    public int ExitCode { get; }
    public string Output { get; }
}
