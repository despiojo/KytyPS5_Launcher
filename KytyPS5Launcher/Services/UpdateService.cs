using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace KytyPS5Launcher.Services;

public sealed record ReleaseInfo(string Tag, Uri DownloadUrl, long Size, string Sha256);
public sealed record UpdateProgress(string Message, double? Percent = null);

public sealed class UpdateService
{
    private const long MaximumDownload = 512L * 1024 * 1024;
    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private readonly string _versions;
    private readonly string _statePath;
    private FileStream? _lock;
    private string? _currentDirectory;

    public string Root { get; }
    public string? InstalledVersion { get; private set; }

    public UpdateService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KytyPS5Launcher")) { }

    public UpdateService(string root)
    {
        Root = Path.GetFullPath(root);
        _versions = Path.Combine(Root, "versions");
        _statePath = Path.Combine(Root, "current.json");
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_versions);
        try { _lock = new FileStream(Path.Combine(Root, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another launcher instance is already running, or its lock cannot be opened.", ex); }
        if (!File.Exists(_statePath)) return;
        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(_statePath));
        string id = state.RootElement.GetProperty("directory").GetString() ?? "";
        string tag = state.RootElement.GetProperty("tag").GetString() ?? "";
        if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("The installation record is invalid. No files have been changed.");
        string directory = Path.Combine(_versions, id);
        if (!File.Exists(Path.Combine(directory, "kyty_launcher.exe")))
            throw new InvalidDataException("The installed launcher is missing. Restore the installation record or files before continuing.");
        _currentDirectory = directory;
        InstalledVersion = tag;
    }

    public async Task<ReleaseInfo> CheckAsync()
    {
        using HttpResponseMessage response = await Client.GetAsync("https://api.github.com/repos/KytyPS5/KytyPS5/releases/latest");
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument json = await JsonDocument.ParseAsync(stream);
        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("The latest release has no tag.");
        var matches = json.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => (a.GetProperty("name").GetString() ?? "").EndsWith("-Windows-x64.zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The latest release does not have exactly one Windows x64 ZIP asset.");
        JsonElement asset = matches[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        long size = asset.GetProperty("size").GetInt64();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/KytyPS5/KytyPS5/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("The release asset URL is not a trusted KytyPS5 GitHub download.");
        if (size <= 0 || size > MaximumDownload || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("The release asset has an invalid size or is missing a SHA-256 digest.");
        return new ReleaseInfo(tag, uri, size, digest[7..]);
    }

    public async Task InstallAsync(ReleaseInfo release, IProgress<UpdateProgress> progress)
    {
        EnsureInitialized();
        EnsureNotRunning();
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_versions, id);
        string download = Path.Combine(Root, id + ".zip");
        string stateTemp = Path.Combine(Root, id + ".json");
        bool committed = false;
        try
        {
            progress.Report(new("Downloading release...", 0));
            await DownloadAsync(release, download, progress);
            await Task.Run(() =>
            {
                EnsureNotRunning();
                Directory.CreateDirectory(directory);
                if (_currentDirectory is not null)
                {
                    progress.Report(new("Copying existing installation and local data..."));
                    CopyDirectory(_currentDirectory, directory);
                }
                progress.Report(new("Extracting release..."));
                ExtractArchive(download, directory);
                if (!File.Exists(Path.Combine(directory, "kyty_launcher.exe")) ||
                    !File.Exists(Path.Combine(directory, "kyty_emulator.exe")))
                    throw new InvalidDataException("The release archive is missing the launcher or emulator.");
                string settings = Path.Combine(directory, "Kyty.ini");
                if (!File.Exists(settings)) File.WriteAllText(settings, "");
                EnsureNotRunning();
                File.Delete(download);
                using (var file = new FileStream(stateTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, new InstallationState(id, release.Tag));
                    file.Flush(true);
                }
                File.Move(stateTemp, _statePath, true);
                committed = true;
                _currentDirectory = directory;
                InstalledVersion = release.Tag;
            });
        }
        finally
        {
            if (File.Exists(download)) File.Delete(download);
            if (File.Exists(stateTemp)) File.Delete(stateTemp);
            if (!committed && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public void Launch()
    {
        EnsureInitialized();
        if (_currentDirectory is null) throw new InvalidOperationException("Install KytyPS5 before launching.");
        string executable = Path.Combine(_currentDirectory, "kyty_launcher.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The installed launcher is missing.", executable);
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _currentDirectory, UseShellExecute = false });
    }

    private void EnsureInitialized()
    {
        if (_lock is null) throw new InvalidOperationException("The installation is not available.");
    }

    private void EnsureNotRunning()
    {
        foreach (string name in new[] { "kyty_launcher", "kyty_emulator", "kyty_updater" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        throw new InvalidOperationException($"Cannot verify whether {name} is running from this installation. Close it before updating.", ex);
                    }
                    if (path is null) throw new InvalidOperationException($"Cannot verify whether {name} is running from this installation.");
                    if (Path.GetFullPath(path).StartsWith(_versions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close KytyPS5 and any running games or updater before installing an update.");
                }
            }
        }
    }

    private static async Task DownloadAsync(ReleaseInfo release, string destination, IProgress<UpdateProgress> progress)
    {
        using HttpResponseMessage response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The download redirected to an insecure URL.");
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("The download size does not match the release metadata.");
        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > release.Size) throw new InvalidDataException("The download exceeds its advertised size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read));
            progress.Report(new("Downloading release...", 100.0 * total / release.Size));
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded release failed its size or SHA-256 verification.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot safely copy a link or junction: {path}");
            string target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(path, target);
            }
            else File.Copy(path, target);
        }
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("The archive contains too many entries.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains(':') ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The archive contains an unsafe entry.");
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path.TrimEnd(Path.DirectorySeparatorChar)))
                throw new InvalidDataException("The archive contains an unsafe or duplicate path.");
            total = checked(total + entry.Length);
            if (total > MaximumExtracted) throw new InvalidDataException("The extracted archive is too large.");
            if (name.EndsWith(Path.DirectorySeparatorChar)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (name.Equals("Kyty.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) continue;
            using Stream input = entry.Open();
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                written += read;
                if (written > entry.Length) throw new InvalidDataException("An archive entry exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("An archive entry is incomplete.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KytyPS5Launcher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record InstallationState(string directory, string tag);
}
