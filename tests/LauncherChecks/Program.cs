using System.Text.Json;
using System.Diagnostics;
using KytyPS5Launcher.Services;

string testRoot = Path.Combine(Path.GetTempPath(), "KytyLocationTests-" + Guid.NewGuid().ToString("N"));
string settingsRoot = Path.Combine(testRoot, "settings");
string destination = Path.Combine(testRoot, "destination");
Directory.CreateDirectory(destination);
try
{
    using (var service = new UpdateService(settingsRoot))
    {
        service.Initialize();
        await service.ChangeLocationAsync(destination);
        Require(service.Root == destination, "Initial folder selection failed.");
    }

    string firstVersion = Guid.NewGuid().ToString("N");
    string secondVersion = Guid.NewGuid().ToString("N");
    string firstDir = Path.Combine(destination, "versions", firstVersion);
    string secondDir = Path.Combine(destination, "versions", secondVersion);
    Directory.CreateDirectory(firstDir);
    Directory.CreateDirectory(secondDir);
    File.WriteAllText(Path.Combine(firstDir, "kyty_launcher.exe"), "launcher");
    File.WriteAllText(Path.Combine(firstDir, "Kyty.ini"), "settings");
    File.WriteAllText(Path.Combine(secondDir, "old-data.txt"), "old version");
    File.WriteAllText(Path.Combine(destination, "current.json"),
        JsonSerializer.Serialize(new { directory = firstVersion, tag = "test-release" }));
    string nextDestination = Path.Combine(testRoot, "next");
    Directory.CreateDirectory(nextDestination);

    using (var service = new UpdateService(settingsRoot))
    {
        service.Initialize();
        Require(service.Root == destination && service.InstalledVersion == "test-release", "Selected folder was not restored.");
        await service.ChangeLocationAsync(nextDestination);
        Require(service.Root == nextDestination && service.InstalledVersion == "test-release", "Moving installation failed.");
        Require(File.ReadAllText(Path.Combine(nextDestination, "versions", firstVersion, "Kyty.ini")) == "settings", "Settings were not copied.");
        Require(File.Exists(Path.Combine(nextDestination, "versions", secondVersion, "old-data.txt")), "Previous versions were not copied.");
        Require(File.Exists(Path.Combine(firstDir, "Kyty.ini")), "Original installation was modified.");

        string nonEmpty = Path.Combine(testRoot, "nonempty");
        Directory.CreateDirectory(nonEmpty);
        File.WriteAllText(Path.Combine(nonEmpty, "keep.txt"), "keep");
        await ExpectFailure(() => service.ChangeLocationAsync(nonEmpty), "non-empty folder");
        await ExpectFailure(() => service.ChangeLocationAsync(Path.Combine(nextDestination, "versions")), "nested folder");
        Require(service.Root == nextDestination && File.ReadAllText(Path.Combine(nonEmpty, "keep.txt")) == "keep", "Rejected folder changed state or data.");

        string failingDestination = Path.Combine(testRoot, "failed");
        Directory.CreateDirectory(failingDestination);
        using (new FileStream(Path.Combine(failingDestination, "launcher.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await ExpectFailure(() => service.ChangeLocationAsync(failingDestination), "occupied destination");
        Require(service.Root == nextDestination, "Failed change replaced the active location.");

        string copyFailure = Path.Combine(testRoot, "copy-failure");
        Directory.CreateDirectory(copyFailure);
        using (new FileStream(Path.Combine(nextDestination, "versions", firstVersion, "Kyty.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ExpectFailure(() => service.ChangeLocationAsync(copyFailure), "failed copy");
        Require(service.Root == nextDestination && File.Exists(Path.Combine(nextDestination, "versions", firstVersion, "Kyty.ini")), "Failed copy changed the active installation.");

        string launcher = Path.Combine(nextDestination, "versions", firstVersion, "kyty_launcher.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), launcher, true);
        using Process process = Process.Start(new ProcessStartInfo(launcher, "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        try
        {
            string whileRunning = Path.Combine(testRoot, "while-running");
            Directory.CreateDirectory(whileRunning);
            await ExpectFailure(() => service.ChangeLocationAsync(whileRunning), "running launcher");
            Require(!Directory.EnumerateFileSystemEntries(whileRunning).Any(), "Running-process check modified the destination.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
        }
    }

    using (var service = new UpdateService(settingsRoot))
    {
        service.Initialize();
        Require(service.Root == nextDestination && service.InstalledVersion == "test-release", "Moved installation was not restored.");
    }
    Console.WriteLine("Location selection, relocation, persistence, rejection and failure safety passed.");
}
finally
{
    if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task ExpectFailure(Func<Task> action, string scenario)
{
    try { await action(); }
    catch (IOException) { return; }
    catch (InvalidOperationException) { return; }
    throw new Exception($"Expected failure for {scenario}.");
}
