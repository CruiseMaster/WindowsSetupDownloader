using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

public record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes);

public class UupNetInstaller
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;
    private static readonly Regex _httpUrlRegex = new(@"https?://[^\s""',\]\)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UupNetInstaller(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        _http.Timeout = TimeSpan.FromMinutes(20); // große Dateien möglich
    }

    // ---------- 1) Metadaten holen ----------
    public async Task<JsonDocument> GetBuildMetadataRawAsync(string updateIdOrUuid, CancellationToken ct = default)
    {
        var url = $"https://api.uupdump.net/get.php?id={Uri.EscapeDataString(updateIdOrUuid)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct);
    }

    // Hilfsfunktion: fetchupd (z. B. aktuellste Retail)
    public async Task<JsonDocument> FetchLatestRawAsync(string arch = "amd64", string ring = "Retail", string flight = "Mainline", CancellationToken ct = default)
    {
        var url = $"https://api.uupdump.net/fetchupd.php?arch={Uri.EscapeDataString(arch)}&ring={Uri.EscapeDataString(ring)}&flight={Uri.EscapeDataString(flight)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct);
    }

    // ---------- 2) Download-URLs aus JSON extrahieren ----------
    // robust: scannt alle string-Werte und sammelt http/https-URLs
    public List<string> ExtractDownloadUrls(JsonDocument doc)
    {
        var urls = new List<string>();
        void Walk(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        Walk(prop.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var v in el.EnumerateArray())
                        Walk(v);
                    break;
                case JsonValueKind.String:
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        foreach (Match m in _httpUrlRegex.Matches(s))
                        {
                            var u = m.Value.TrimEnd(',', ')', ']', '"', '\'');
                            if (!urls.Contains(u)) urls.Add(u);
                        }
                    }
                    break;
            }
        }
        Walk(doc.RootElement);
        return urls;
    }

    // Wenn die JSON strukturierte "files" mit sha256 hat, parse map filename->sha256
    public Dictionary<string, string> ExtractSha256Map(JsonDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // naive Suche nach properties named "sha256" in proximity of filename keys
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // try to detect pattern { "filename.ext": { "size":..., "sha256": "..." }, ... }
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        // look for sha256 inside prop.Value
                        if (prop.Value.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.String)
                        {
                            var filename = prop.Name;
                            var sha = shaEl.GetString() ?? "";
                            map[filename] = sha;
                        }
                    }
                }
                // continue walking deeper
                foreach (var prop in el.EnumerateObject())
                    Walk(prop.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in el.EnumerateArray()) Walk(v);
            }
        }
        Walk(doc.RootElement);
        return map;
    }

    // ---------- 3) Parallel-Downloader mit Fortschritt ----------
    public async Task DownloadUrlsAsync(IEnumerable<string> urls, string destFolder, IProgress<DownloadProgress>? progress = null, int maxParallel = 4, int maxRetries = 5, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destFolder);
        var urlList = urls.ToList();
        using var sem = new SemaphoreSlim(maxParallel);

        var tasks = urlList.Select(async url =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var fileName = GetSafeFileNameFromUrl(url);
                var destPath = Path.Combine(destFolder, fileName);

                // already exists? skip or re-download? we'll check size / allow overwrite by default
                int attempt = 0;
                while (true)
                {
                    attempt++;
                    try
                    {
                        await DownloadFileWithProgressAsync(url, destPath, progress, ct);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (attempt >= maxRetries) throw new Exception($"Download {url} failed after {attempt} attempts: {ex.Message}", ex);
                        // backoff
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, Math.Min(6, attempt))), ct);
                    }
                }

            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    private static string GetSafeFileNameFromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            var name = Path.GetFileName(u.LocalPath);
            if (string.IsNullOrEmpty(name))
            {
                // fallback: host + hash
                var h = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))).Substring(0, 8);
                name = $"{u.Host}_{h}.bin";
            }
            // sanitize
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
        catch { return Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))).Substring(0, 12) + ".bin"; }
    }

    private async Task DownloadFileWithProgressAsync(string url, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        var fileName = Path.GetFileName(destPath);

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            progress?.Report(new DownloadProgress(fileName, totalRead, total));
        }
    }

    // ---------- 4) Prüfsummenprüfung (SHA256) ----------
    public async Task<Dictionary<string, bool>> VerifySha256Async(string folder, Dictionary<string, string> expectedMap, CancellationToken ct = default)
    {
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in expectedMap)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(folder, kv.Key);
            if (!File.Exists(file))
            {
                results[kv.Key] = false;
                continue;
            }
            using var fs = File.OpenRead(file);
            var computed = await ComputeSha256Async(fs, ct);
            results[kv.Key] = string.Equals(computed, kv.Value, StringComparison.OrdinalIgnoreCase);
        }
        return results;
    }

    private static async Task<string> ComputeSha256Async(Stream s, CancellationToken ct)
    {
        s.Position = 0;
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    // ---------- 5) Konvertierung anstoßen (externes Tool) ----------
    // erwartet: pathToConverter (z.B. "uup-converter-wimlib.cmd" oder "wimlib-imagex.exe")
    // argsTemplate: z.B. "{inputFolder} {outputIso}" - Platzhalter werden ersetzt
    public async Task<int> RunExternalConverterAsync(string converterPath, string argsTemplate, string inputFolder, string outputFile, CancellationToken ct = default)
    {
        if (!File.Exists(converterPath))
            throw new FileNotFoundException("Converter not found", converterPath);

        var args = argsTemplate.Replace("{inputFolder}", Quote(inputFolder)).Replace("{outputFile}", Quote(outputFile));
        var psi = new ProcessStartInfo(converterPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var tcs = new TaskCompletionSource<int>();

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start converter process");
        _ = Task.Run(async () =>
        {
            var buf = new char[4096];
            try
            {
                // optionally read outputs
                while (!proc.HasExited)
                {
                    await Task.Delay(200, ct);
                }
            }
            catch { }
        }, ct);

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static string Quote(string s) => $"\"{s}\"";

    // ---------- 6) High level helper: Complete workflow ----------
    /// <summary>
    /// Full flow:
    ///  - Get metadata (get.php)
    ///  - extract URLs
    ///  - download into destFolder
    ///  - verify sha256 if present
    ///  - optionally call external converter
    /// </summary>
    public async Task RunFullWorkflowAsync(string updateIdOrUuid, string destFolder, IProgress<DownloadProgress>? progress = null, string? converterPath = null, string? converterArgsTemplate = null, CancellationToken ct = default)
    {
        // 1) metadata
        var meta = await GetBuildMetadataRawAsync(updateIdOrUuid, ct);

        // 2) get url list
        var urls = ExtractDownloadUrls(meta);
        if (urls.Count == 0)
            throw new InvalidOperationException("Keine Download-URLs im Build-Metadaten gefunden.");

        // 3) determine friendly file names from JSON: attempt to map filenames in JSON to URLs
        // fallback: Download file names from URLs
        // 4) download
        await DownloadUrlsAsync(urls, destFolder, progress, maxParallel: 4, ct: ct);

        // 5) verify if sha256 present
        var shaMap = ExtractSha256Map(meta);
        if (shaMap.Count > 0)
        {
            var verify = await VerifySha256Async(destFolder, shaMap, ct);
            var bad = verify.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
            if (bad.Any())
                throw new Exception("Checksum verification failed for: " + string.Join(", ", bad));
        }

        // 6) convert if requested
        if (!string.IsNullOrEmpty(converterPath) && !string.IsNullOrEmpty(converterArgsTemplate))
        {
            var outputIso = Path.Combine(destFolder, "output.iso");
            var code = await RunExternalConverterAsync(converterPath, converterArgsTemplate, destFolder, outputIso, ct);
            if (code != 0) throw new Exception($"External converter returned exit code {code}");
        }
    }
}
