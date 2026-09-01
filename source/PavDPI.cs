// PavDPI 2.0 - Combined source snapshot
// Full structured source: PavDPI-Kaynak.zip

// ============================================================
// Source: src/PavDPI.Installer/InstallerCore.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal sealed class InstallResult
{
    public bool Success { get; private set; }
    public string Message { get; private set; }

    private InstallResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public static InstallResult Ok(string message) { return new InstallResult(true, message); }
    public static InstallResult Fail(string message) { return new InstallResult(false, message); }
}

internal sealed class PavDPIStatus
{
    public bool Installed { get; set; }
    public bool ServiceRunning { get; set; }
    public bool EngineRunning { get; set; }
    public string EngineState { get; set; }
    public string ProfileName { get; set; }
    public string Detail { get; set; }
}

internal sealed class ConnectionProbeResult
{
    public string Host { get; set; }
    public string Addresses { get; set; }
    public bool DnsResolved { get; set; }
    public bool PoisonedAddress { get; set; }
    public bool Port443Open { get; set; }
    public int Milliseconds { get; set; }

    public override string ToString()
    {
        string dnsState = !DnsResolved ? "DNS HATA" : (PoisonedAddress ? "ENGEL IP'SI" : "DNS OK");
        string portState = Port443Open ? "443 OK" : "443 KAPALI";
        return Host + " | " + dnsState + " | " + portState + " | " + Milliseconds + " ms | " + Addresses;
    }
}

internal static class InstallerCore
{
    private const string ServiceName = "PavDPI";
    private const string PayloadResourceName = "PavDPI.Payload.zip";
    private const string PoisonedAddress = "195.175.254.2";
    private const string ExpectedEngineHash = "7ACDE0DC3D40E448B70B08D661F633A61DDC94E9292EE3DCF447C377162A455C";
    private static readonly string ProgramFilesDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string InstallDirectory = Path.Combine(ProgramFilesDirectory, "PavDPI");
    private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PavDPI");
    private static readonly string InstallerLogPath = Path.Combine(DataDirectory, "installer.log");
    private static readonly string StatePath = Path.Combine(DataDirectory, "state.txt");
    private static readonly object LogSync = new object();

    private static readonly string[] ProbeHosts =
    {
        "discord.com",
        "gateway.discord.gg",
        "www.roblox.com",
        "games.roblox.com",
        "thumbnails.roblox.com",
        "account.proton.me",
        "mail.proton.me"
    };

    public static InstallResult Install(PavDPIProfile requestedProfile, Action<string> progress)
    {
        string temporaryDirectory = null;
        string stagingDirectory = null;
        string backupDirectory = null;
        ServiceSnapshot previousPavDpi = null;

        try
        {
            EnsureAdministrator();
            if (requestedProfile == null) { throw new ArgumentNullException("requestedProfile"); }
            Directory.CreateDirectory(DataDirectory);
            previousPavDpi = WindowsServiceManager.Capture(ServiceName);
            Log("Install started. RequestedProfile=" + requestedProfile.Id);

            Report(progress, "Tek dosyalik paket cikartiliyor ve SHA-256 ile dogrulaniyor...");
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "PavDPI-Install-" + Guid.NewGuid().ToString("N"));
            string extractedDirectory = Path.Combine(temporaryDirectory, "payload");
            Directory.CreateDirectory(extractedDirectory);
            ExtractEmbeddedPayload(extractedDirectory);
            VerifyPayload(extractedDirectory);

            stagingDirectory = Path.Combine(ProgramFilesDirectory, "PavDPI.new-" + Guid.NewGuid().ToString("N"));
            EnsureSafeChildPath(stagingDirectory, ProgramFilesDirectory);
            CopyDirectory(extractedDirectory, stagingDirectory);

            Report(progress, "Eski PavDPI hizmeti durduruluyor...");
            WindowsServiceManager.StopAndWait(ServiceName, TimeSpan.FromSeconds(20));
            KillInstalledEngines();

            if (Directory.Exists(InstallDirectory))
            {
                string backupRoot = Path.Combine(DataDirectory, "backups");
                Directory.CreateDirectory(backupRoot);
                backupDirectory = Path.Combine(backupRoot, "PavDPI-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                EnsureSafeChildPath(backupDirectory, backupRoot);
                Directory.Move(InstallDirectory, backupDirectory);
                Log("Previous install moved to " + backupDirectory);
            }

            Directory.Move(stagingDirectory, InstallDirectory);
            stagingDirectory = null;
            string noticesPath = Path.Combine(InstallDirectory, "THIRD-PARTY-NOTICES.txt");
            if (File.Exists(noticesPath))
            {
                File.SetAttributes(noticesPath, File.GetAttributes(noticesPath) | FileAttributes.Hidden);
            }

            string serviceExecutable = Path.Combine(InstallDirectory, "PavDPI Service.exe");
            Report(progress, "Servis ve motor dosyalari kendi kendini test ediyor...");
            ProcessResult selfTest = RunProcess(serviceExecutable, "--self-test", InstallDirectory, 15000);
            if (selfTest.ExitCode != 0)
            {
                throw new InvalidOperationException("Servis oz-testi basarisiz: " + selfTest.Output);
            }

            Report(progress, "PavDPI Service Windows acilisina kaydediliyor...");
            WindowsServiceManager.InstallOrUpdate(
                ServiceName,
                "PavDPI Service",
                serviceExecutable,
                "PavDPI Service - penceresiz baglanti motoru");

            IList<PavDPIProfile> candidates = requestedProfile.IsAutomatic
                ? PavDPIProfile.AutomaticCandidates()
                : new List<PavDPIProfile> { requestedProfile };

            PavDPIProfile selectedProfile = null;
            IList<ConnectionProbeResult> lastResults = null;
            foreach (PavDPIProfile candidate in candidates)
            {
                Report(progress, "Bağlantı ayarları kontrol ediliyor…");
                ApplyProfile(candidate);
                DeleteStateFile();
                WindowsServiceManager.StopAndWait(ServiceName, TimeSpan.FromSeconds(15));
                WindowsServiceManager.StartAndWait(ServiceName, TimeSpan.FromSeconds(20));

                if (!WaitForStableEngine(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(6)))
                {
                    Report(progress, "Motor kararli calismadi; siradaki profil denenecek.");
                    continue;
                }

                FlushDns();
                lastResults = ProbeConnections();
                foreach (ConnectionProbeResult result in lastResults) { Report(progress, result.ToString()); }
                if (ConnectionsHealthy(lastResults))
                {
                    selectedProfile = candidate;
                    break;
                }
                Report(progress, "Bu profil tum baglanti kontrollerini gecemedi.");
            }

            if (selectedProfile == null)
            {
                string lastSummary = lastResults == null
                    ? "Baglanti testi baslatilamadi."
                    : String.Join("; ", lastResults.Select(delegate(ConnectionProbeResult result) { return result.ToString(); }).ToArray());
                throw new InvalidOperationException("Hicbir ayar Discord ve Roblox testlerini birlikte gecemedi. " + lastSummary);
            }

            CleanupOldBackups(0);
            FlushDns();
            Log("Install completed. SelectedProfile=" + selectedProfile.Id);
            return InstallResult.Ok(
                "PavDPI kuruldu ve doğrulandı.\r\n\r\nWindows açılışında otomatik olarak çalışacak.");
        }
        catch (Exception exception)
        {
            Log("Install failed: " + exception);
            try
            {
                Report(progress, "Kurulum basarisiz; onceki durum geri yukleniyor...");
                WindowsServiceManager.Delete(ServiceName);
                KillInstalledEngines();
                if (Directory.Exists(InstallDirectory)) { SafeDeleteDirectory(InstallDirectory, ProgramFilesDirectory); }
                if (!String.IsNullOrWhiteSpace(backupDirectory) && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, InstallDirectory);
                    backupDirectory = null;
                }
                WindowsServiceManager.Restore(ServiceName, previousPavDpi);
                FlushDns();
                Log("Rollback completed.");
            }
            catch (Exception rollbackException)
            {
                Log("Rollback error: " + rollbackException);
            }
            return InstallResult.Fail("Kurulum tamamlanamadi: " + exception.Message + "\r\n\r\nGunluk: " + InstallerLogPath);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory, ProgramFilesDirectory);
            TryDeleteDirectory(temporaryDirectory, Path.GetTempPath());
        }
    }

    public static InstallResult Uninstall(Action<string> progress)
    {
        try
        {
            EnsureAdministrator();
            Log("Uninstall started.");
            Report(progress, "PavDPI Service durduruluyor...");
            WindowsServiceManager.Delete(ServiceName);
            KillInstalledEngines();
            if (Directory.Exists(InstallDirectory))
            {
                Report(progress, "Program dosyalari kaldiriliyor...");
                SafeDeleteDirectory(InstallDirectory, ProgramFilesDirectory);
            }
            FlushDns();
            Log("Uninstall completed.");
            return InstallResult.Ok("PavDPI kaldirildi. Teshis gunlukleri ProgramData\\PavDPI klasorunde korundu.");
        }
        catch (Exception exception)
        {
            Log("Uninstall failed: " + exception);
            return InstallResult.Fail("Kaldirma tamamlanamadi: " + exception.Message + "\r\n\r\nGunluk: " + InstallerLogPath);
        }
    }

    public static PavDPIStatus QueryStatus()
    {
        PavDPIStatus status = new PavDPIStatus();
        status.Installed = WindowsServiceManager.Exists(ServiceName) && File.Exists(Path.Combine(InstallDirectory, "PavDPI Service.exe"));
        status.ServiceRunning = WindowsServiceManager.IsRunning(ServiceName);
        status.EngineState = "UNKNOWN";
        status.ProfileName = ReadTextFile(Path.Combine(InstallDirectory, "config", "profile.name"), "-");
        status.Detail = "Canli durum dosyasi bulunamadi.";

        Dictionary<string, string> state = ReadKeyValueFile(StatePath);
        if (state.ContainsKey("status")) { status.EngineState = state["status"]; }
        if (state.ContainsKey("profile")) { status.ProfileName = state["profile"]; }
        if (state.ContainsKey("detail")) { status.Detail = state["detail"]; }
        int processId;
        Int32.TryParse(state.ContainsKey("pid") ? state["pid"] : "0", out processId);
        status.EngineRunning = status.ServiceRunning && String.Equals(status.EngineState, "ENGINE_RUNNING", StringComparison.OrdinalIgnoreCase) && IsProcessAlive(processId);
        return status;
    }

    public static bool RequiresUpgrade()
    {
        if (!QueryStatus().Installed) { return true; }
        string installedProfileName = ReadTextFile(Path.Combine(InstallDirectory, "config", "profile.name"), String.Empty);
        if (!String.Equals(installedProfileName, "Otomatik", StringComparison.OrdinalIgnoreCase)) { return true; }
        string installedEngine = Path.Combine(InstallDirectory, "engine", "PavDPI Engine.exe");
        if (!File.Exists(installedEngine)) { return true; }
        try { return !String.Equals(ComputeSha256(installedEngine), ExpectedEngineHash, StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    public static IList<ConnectionProbeResult> ProbeConnections()
    {
        List<ConnectionProbeResult> results = new List<ConnectionProbeResult>();
        foreach (string host in ProbeHosts)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            ConnectionProbeResult result = new ConnectionProbeResult();
            result.Host = host;
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                result.DnsResolved = addresses.Length > 0;
                result.Addresses = String.Join(",", addresses.Select(delegate(IPAddress address) { return address.ToString(); }).ToArray());
                result.PoisonedAddress = addresses.Any(delegate(IPAddress address) { return address.ToString() == PoisonedAddress; });
                result.Port443Open = !result.PoisonedAddress && TryConnect(addresses, 443, 4500);
            }
            catch (Exception exception)
            {
                result.Addresses = "HATA: " + exception.Message;
                result.DnsResolved = false;
                result.PoisonedAddress = false;
                result.Port443Open = false;
            }
            stopwatch.Stop();
            result.Milliseconds = (int)stopwatch.ElapsedMilliseconds;
            results.Add(result);
        }
        return results;
    }

    public static bool ConnectionsHealthy(IList<ConnectionProbeResult> results)
    {
        if (results == null || results.Count != ProbeHosts.Length) { return false; }
        return results.All(delegate(ConnectionProbeResult result)
        {
            return result.DnsResolved && !result.PoisonedAddress && result.Port443Open;
        });
    }

    public static string GetInstallerLogPath() { return InstallerLogPath; }

    private static void EnsureAdministrator()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("PavDPI kurulumu icin Windows yonetici onayi gerekir.");
            }
        }
    }

    private static void ApplyProfile(PavDPIProfile profile)
    {
        if (profile == null || profile.IsAutomatic || String.IsNullOrWhiteSpace(profile.Arguments))
        {
            throw new InvalidOperationException("Gecersiz PavDPI profili.");
        }
        if (profile.Arguments.Contains("--set-ttl") && !profile.Arguments.Contains("--blacklist"))
        {
            throw new InvalidOperationException("Guvenlik denetimi: sabit TTL yalnizca hedef listesiyle kullanilabilir.");
        }
        string configDirectory = Path.Combine(InstallDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "profile.args"), profile.Arguments + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(configDirectory, "profile.name"), profile.DisplayName + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void ExtractEmbeddedPayload(string destinationDirectory)
    {
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName))
        {
            if (payload == null) { throw new InvalidOperationException("Gomulu PavDPI paketi bulunamadi."); }
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (String.IsNullOrWhiteSpace(normalizedName)) { continue; }
                    string destinationPath = SafeCombine(destinationDirectory, normalizedName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }
    }

    private static void VerifyPayload(string payloadDirectory)
    {
        string manifestPath = Path.Combine(payloadDirectory, "manifest.sha256");
        if (!File.Exists(manifestPath)) { throw new FileNotFoundException("Paket butunluk listesi bulunamadi.", manifestPath); }

        string[] required =
        {
            "PavDPI Service.exe",
            "engine\\PavDPI Engine.exe",
            "engine\\WinDivert.dll",
            "engine\\WinDivert64.sys",
            "config\\profile.args",
            "config\\profile.name",
            "config\\targets.txt",
            "THIRD-PARTY-NOTICES.txt"
        };
        HashSet<string> manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(manifestPath, Encoding.UTF8))
        {
            if (String.IsNullOrWhiteSpace(line)) { continue; }
            int separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64) { throw new InvalidDataException("Gecersiz butunluk satiri: " + line); }
            string expectedHash = line.Substring(0, 64).ToUpperInvariant();
            string relativePath = line.Substring(separator + 2).Replace('/', Path.DirectorySeparatorChar);
            if (!manifestFiles.Add(relativePath)) { throw new InvalidDataException("Tekrarlanan paket yolu: " + relativePath); }
            string fullPath = SafeCombine(payloadDirectory, relativePath);
            if (!File.Exists(fullPath)) { throw new FileNotFoundException("Paket dosyasi eksik: " + relativePath, fullPath); }
            if (!String.Equals(expectedHash, ComputeSha256(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Paket dosyasi dogrulanamadi: " + relativePath);
            }
        }

        foreach (string relativePath in required)
        {
            if (!manifestFiles.Contains(relativePath)) { throw new InvalidDataException("Zorunlu paket dosyasi manifestte yok: " + relativePath); }
        }
        foreach (string file in Directory.GetFiles(payloadDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(payloadDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
            if (!String.Equals(relative, "manifest.sha256", StringComparison.OrdinalIgnoreCase) && !manifestFiles.Contains(relative))
            {
                throw new InvalidDataException("Manifest disi paket dosyasi: " + relative);
            }
        }
    }

    private static bool WaitForStableEngine(TimeSpan maximumWait, TimeSpan requiredStableTime)
    {
        DateTime deadline = DateTime.UtcNow + maximumWait;
        DateTime? stableSince = null;
        int stableProcessId = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (!WindowsServiceManager.IsRunning(ServiceName))
            {
                stableSince = null;
                Thread.Sleep(500);
                continue;
            }
            Dictionary<string, string> state = ReadKeyValueFile(StatePath);
            string stateValue = state.ContainsKey("status") ? state["status"] : String.Empty;
            int processId;
            Int32.TryParse(state.ContainsKey("pid") ? state["pid"] : "0", out processId);
            if (String.Equals(stateValue, "ENGINE_RUNNING", StringComparison.OrdinalIgnoreCase) && IsProcessAlive(processId))
            {
                if (!stableSince.HasValue || stableProcessId != processId)
                {
                    stableSince = DateTime.UtcNow;
                    stableProcessId = processId;
                }
                if (DateTime.UtcNow - stableSince.Value >= requiredStableTime) { return true; }
            }
            else
            {
                stableSince = null;
                stableProcessId = 0;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) { return false; }
        try
        {
            using (Process process = Process.GetProcessById(processId)) { return !process.HasExited; }
        }
        catch { return false; }
    }

    private static bool TryConnect(IPAddress[] addresses, int port, int timeoutMilliseconds)
    {
        if (addresses == null || addresses.Length == 0) { return false; }
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        foreach (IPAddress address in addresses)
        {
            int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
            if (remaining <= 0) { break; }
            int attemptsLeft = Math.Max(1, addresses.Length);
            int attemptTimeout = Math.Max(350, remaining / attemptsLeft);
            using (Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                IAsyncResult pending = null;
                try
                {
                    pending = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(attemptTimeout)) { continue; }
                    socket.EndConnect(pending);
                    if (socket.Connected) { return true; }
                }
                catch { }
                finally { if (pending != null) { pending.AsyncWaitHandle.Close(); } }
            }
        }
        return false;
    }

    private static void KillInstalledEngines()
    {
        string installPrefix = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string processName in new string[] { "PavDPI Engine", "pavdpi" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string executablePath = process.MainModule.FileName;
                    if (!Path.GetFullPath(executablePath).StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                    Log("Stopping installed engine PID=" + process.Id + " Path=" + executablePath);
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception exception) { Log("Installed engine stop error PID=" + process.Id + ": " + exception.Message); }
                finally { process.Dispose(); }
            }
        }
    }

    private static void FlushDns()
    {
        try { RunProcess(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns", Environment.SystemDirectory, 10000); }
        catch (Exception exception) { Log("DNS flush warning: " + exception.Message); }
    }

    private static ProcessResult RunProcess(string executable, string arguments, string workingDirectory, int timeoutMilliseconds)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = executable;
        startInfo.Arguments = arguments;
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using (Process process = Process.Start(startInfo))
        {
            if (process == null) { throw new InvalidOperationException("Komut baslatilamadi: " + executable); }
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException("Komut zaman asimina ugradi: " + executable);
            }
            Log("Command: " + Path.GetFileName(executable) + " " + arguments + " Exit=" + process.ExitCode + " Output=" + CleanLine(output));
            return new ProcessResult(process.ExitCode, output.Trim());
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourceFile in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)), true);
        }
        foreach (string sourceChild in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(sourceChild, Path.Combine(destinationDirectory, Path.GetFileName(sourceChild)));
        }
    }

    private static string SafeCombine(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.IndexOf(':') >= 0)
        {
            throw new InvalidDataException("Paket yolu guvenli degil: " + relativePath);
        }
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Paket yolu guvenli degil: " + relativePath);
        }
        return fullPath;
    }

    private static void EnsureSafeChildPath(string path, string parent)
    {
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Guvenli olmayan dosya hedefi: " + fullPath);
        }
    }

    private static void SafeDeleteDirectory(string path, string allowedParent)
    {
        EnsureSafeChildPath(path, allowedParent);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Yeniden yonlendirilmis klasor silinmedi: " + path);
        }
        Directory.Delete(path, true);
    }

    private static void TryDeleteDirectory(string path, string allowedParent)
    {
        if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { return; }
        try { SafeDeleteDirectory(path, allowedParent); } catch (Exception exception) { Log("Temporary cleanup warning: " + exception.Message); }
    }

    private static string ComputeSha256(string path)
    {
        using (SHA256 sha256 = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", String.Empty);
        }
    }

    private static Dictionary<string, string> ReadKeyValueFile(string path)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) { return values; }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int separator = line.IndexOf('=');
                if (separator > 0) { values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim(); }
            }
        }
        catch { }
        return values;
    }

    private static string ReadTextFile(string path, string fallback)
    {
        try { return File.Exists(path) ? CleanLine(File.ReadAllText(path, Encoding.UTF8)).Trim() : fallback; }
        catch { return fallback; }
    }

    private static void DeleteStateFile()
    {
        try { if (File.Exists(StatePath)) { File.Delete(StatePath); } } catch { }
    }

    private static void CleanupOldBackups(int keepCount)
    {
        string backupRoot = Path.Combine(DataDirectory, "backups");
        if (!Directory.Exists(backupRoot)) { return; }
        DirectoryInfo[] backups = new DirectoryInfo(backupRoot).GetDirectories("PavDPI-*")
            .OrderByDescending(delegate(DirectoryInfo directory) { return directory.CreationTimeUtc; })
            .ToArray();
        for (int index = keepCount; index < backups.Length; index++)
        {
            try { SafeDeleteDirectory(backups[index].FullName, backupRoot); }
            catch (Exception exception) { Log("Backup cleanup warning: " + exception.Message); }
        }
    }

    private static void Report(Action<string> progress, string message)
    {
        Log(message);
        if (progress != null) { progress(message); }
    }

    private static string CleanLine(string value)
    {
        return value == null ? String.Empty : value.Replace("\r", " ").Replace("\n", " ");
    }

    private static void Log(string message)
    {
        lock (LogSync)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(InstallerLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private sealed class ProcessResult
    {
        public int ExitCode { get; private set; }
        public string Output { get; private set; }

        public ProcessResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }
    }
}

// ============================================================
// Source: src/PavDPI.Installer/MainForm.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    private const int WmNcButtonDown = 0xA1;
    private const int HtCaption = 0x2;
    private readonly Color background = Color.FromArgb(30, 30, 30);
    private readonly Color panel = Color.FromArgb(40, 40, 43);
    private readonly Color panelLight = Color.FromArgb(54, 54, 58);
    private readonly Color accent = Color.FromArgb(0, 120, 212);
    private readonly Color accentGreen = Color.FromArgb(102, 176, 116);
    private readonly Color danger = Color.FromArgb(210, 92, 102);
    private readonly Color textPrimary = Color.FromArgb(245, 245, 245);
    private readonly Color textSecondary = Color.FromArgb(181, 181, 185);

    private Label statusLabel;
    private Button installButton;
    private Button uninstallButton;
    private Button testButton;
    private ProgressBar progressBar;
    private PavDPIProfile automaticProfile;
    private bool busy;

    public MainForm()
    {
        Text = "PavDPI 2.0";
        ClientSize = new Size(620, 290);
        MinimumSize = new Size(620, 290);
        MaximumSize = new Size(620, 290);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        DoubleBuffered = true;
        BackColor = background;
        ForeColor = textPrimary;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        automaticProfile = PavDPIProfile.Find("auto");
        if (automaticProfile == null) { throw new InvalidOperationException("Otomatik PavDPI profili bulunamadı."); }
        BuildInterface();
        UpdateRoundedRegion();
        MouseDown += BeginWindowDrag;
        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    private void BuildInterface()
    {
        GlobeControl globe = new GlobeControl();
        globe.Location = new Point(28, 20);
        globe.Size = new Size(58, 58);
        globe.BackColor = background;
        globe.ForeColor = textPrimary;
        globe.MouseDown += BeginWindowDrag;
        Controls.Add(globe);

        Label title = new Label();
        title.Text = "PavDPI";
        title.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Regular, GraphicsUnit.Point);
        title.AutoSize = true;
        title.Location = new Point(98, 31);
        title.ForeColor = textPrimary;
        title.MouseDown += BeginWindowDrag;
        Controls.Add(title);

        Button minimizeButton = CreateWindowButton("\u2014", new Point(540, 15), panelLight);
        minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
        Controls.Add(minimizeButton);

        Button closeButton = CreateWindowButton("\u2715", new Point(578, 15), danger);
        closeButton.Click += delegate { Close(); };
        Controls.Add(closeButton);

        RoundedPanel statusPanel = new RoundedPanel();
        statusPanel.CornerRadius = 12;
        statusPanel.Location = new Point(24, 96);
        statusPanel.Size = new Size(572, 86);
        statusPanel.BackColor = panel;
        Controls.Add(statusPanel);

        statusLabel = new Label();
        statusLabel.Text = "Kontrol ediliyor…";
        statusLabel.AutoEllipsis = true;
        statusLabel.Location = new Point(20, 16);
        statusLabel.Size = new Size(532, 28);
        statusLabel.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Regular, GraphicsUnit.Point);
        statusLabel.ForeColor = textPrimary;
        statusPanel.Controls.Add(statusLabel);

        Label statusDescription = new Label();
        statusDescription.Text = "PavDPI uygun ayarı seçer ve Windows ile birlikte çalışır.";
        statusDescription.Location = new Point(20, 52);
        statusDescription.Size = new Size(532, 20);
        statusDescription.ForeColor = textSecondary;
        statusPanel.Controls.Add(statusDescription);

        installButton = CreateButton("Kur veya onar", accent, new Point(24, 204), new Size(184, 44));
        installButton.Click += InstallButton_Click;
        Controls.Add(installButton);

        testButton = CreateButton("Bağlantıyı test et", panelLight, new Point(218, 204), new Size(200, 44));
        testButton.Click += TestButton_Click;
        Controls.Add(testButton);

        uninstallButton = CreateButton("Kaldır", panelLight, new Point(428, 204), new Size(168, 44));
        uninstallButton.ForeColor = Color.FromArgb(245, 164, 169);
        uninstallButton.Click += UninstallButton_Click;
        Controls.Add(uninstallButton);

        progressBar = new ProgressBar();
        progressBar.Location = new Point(24, 267);
        progressBar.Size = new Size(572, 4);
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.MarqueeAnimationSpeed = 25;
        progressBar.Visible = false;
        Controls.Add(progressBar);

    }

    private Button CreateButton(string text, Color color, Point location, Size size)
    {
        RoundedButton button = new RoundedButton();
        button.CornerRadius = 8;
        button.Text = text;
        button.Location = location;
        button.Size = size;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        return button;
    }

    private Button CreateWindowButton(string text, Point location, Color hoverColor)
    {
        Button button = new Button();
        button.Text = text;
        button.Location = location;
        button.Size = new Size(30, 30);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = hoverColor;
        button.BackColor = background;
        button.ForeColor = textPrimary;
        button.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        return button;
    }

    private void BeginWindowDrag(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left) { return; }
        ReleaseCapture();
        SendMessage(Handle, WmNcButtonDown, HtCaption, 0);
    }

    private void UpdateRoundedRegion()
    {
        using (GraphicsPath path = RoundedShape.CreatePath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 18))
        {
            Region previous = Region;
            Region = new Region(path);
            if (previous != null) { previous.Dispose(); }
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (ClientSize.Width > 0 && ClientSize.Height > 0) { UpdateRoundedRegion(); }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int ClassStyleDropShadow = 0x00020000;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= ClassStyleDropShadow;
            return parameters;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, int wordParameter, int longParameter);

    private async void MainForm_Shown(object sender, EventArgs eventArgs)
    {
        RefreshStatus();
        if (InstallerCore.RequiresUpgrade())
        {
            await InstallOrRepairAsync(true);
        }
    }

    private async void InstallButton_Click(object sender, EventArgs eventArgs)
    {
        await InstallOrRepairAsync(false);
    }

    private async Task InstallOrRepairAsync(bool startedAutomatically)
    {
        SetBusy(true);
        AppendActivity(startedAutomatically
            ? "Eski kurulum algılandı; PavDPI otomatik olarak yenileniyor…"
            : "Kurulum başlatılıyor…");
        InstallResult result = await Task.Run(delegate { return InstallerCore.Install(automaticProfile, AppendActivity); });
        AppendActivity(result.Message);
        SetBusy(false);
        RefreshStatus();
        statusLabel.ForeColor = result.Success ? accentGreen : danger;
        MessageBox.Show(
            result.Message,
            result.Success ? "PavDPI hazır" : "PavDPI kurulumu tamamlanamadı",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        if (result.Success) { Close(); }
    }

    private async void UninstallButton_Click(object sender, EventArgs eventArgs)
    {
        DialogResult confirmation = MessageBox.Show(
            "PavDPI hizmeti ve Program Files altındaki dosyaları kaldırılsın mı?",
            "PavDPI'yi kaldır",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.Yes) { return; }

        SetBusy(true);
        AppendActivity("Kaldırma başlatıldı.");
        InstallResult result = await Task.Run(delegate { return InstallerCore.Uninstall(AppendActivity); });
        AppendActivity(result.Message);
        SetBusy(false);
        RefreshStatus();
    }

    private async void TestButton_Click(object sender, EventArgs eventArgs)
    {
        SetBusy(true);
        AppendActivity("Discord ve Roblox bağlantıları test ediliyor…");
        IList<ConnectionProbeResult> results = await Task.Run(delegate { return InstallerCore.ProbeConnections(); });
        bool healthy = InstallerCore.ConnectionsHealthy(results);
        AppendActivity(healthy ? "Tüm bağlantı testleri başarılı." : "Bağlantı testi başarısız. Kur veya onar ile otomatik onarımı dene.");
        statusLabel.ForeColor = healthy ? accentGreen : danger;
        SetBusy(false);
    }

    private void RefreshStatus()
    {
        PavDPIStatus status = InstallerCore.QueryStatus();
        if (!status.Installed)
        {
            statusLabel.Text = "Kurulu değil";
            statusLabel.ForeColor = textSecondary;
            return;
        }
        if (status.ServiceRunning && status.EngineRunning)
        {
            statusLabel.Text = "Çalışıyor";
            statusLabel.ForeColor = accentGreen;
        }
        else
        {
            statusLabel.Text = "Sorun algılandı  ·  Servis " + (status.ServiceRunning ? "açık" : "kapalı") + "  ·  Motor " + status.EngineState + "  ·  " + status.Detail;
            statusLabel.ForeColor = danger;
        }
    }

    private void SetBusy(bool value)
    {
        if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), value); return; }
        busy = value;
        progressBar.Visible = value;
        installButton.Enabled = !value;
        uninstallButton.Enabled = !value;
        testButton.Enabled = !value;
        UseWaitCursor = value;
    }

    private void AppendActivity(string message)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(AppendActivity), message); return; }
        statusLabel.Text = message.Replace("\r", " ").Replace("\n", " ");
    }

    private void MainForm_FormClosing(object sender, FormClosingEventArgs eventArgs)
    {
        if (!busy) { return; }
        eventArgs.Cancel = true;
        AppendActivity("İşlem tamamlanana kadar pencere açık kalmalı.");
    }
}

internal static class RoundedShape
{
    public static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = Math.Max(2, radius * 2);
        Rectangle rectangle = new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        diameter = Math.Min(diameter, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Apply(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0) { return; }
        using (GraphicsPath path = CreatePath(control.ClientRectangle, radius))
        {
            Region previous = control.Region;
            control.Region = new Region(path);
            if (previous != null) { previous.Dispose(); }
        }
    }
}

internal sealed class RoundedPanel : Panel
{
    private int cornerRadius = 14;

    public int CornerRadius
    {
        get { return cornerRadius; }
        set { cornerRadius = Math.Max(1, value); RoundedShape.Apply(this, cornerRadius); }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        RoundedShape.Apply(this, cornerRadius);
    }
}

internal sealed class RoundedButton : Button
{
    private int cornerRadius = 10;

    public int CornerRadius
    {
        get { return cornerRadius; }
        set { cornerRadius = Math.Max(1, value); RoundedShape.Apply(this, cornerRadius); }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        RoundedShape.Apply(this, cornerRadius);
    }
}

internal sealed class GlobeControl : Control
{
    public GlobeControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Graphics graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF badge = new RectangleF(1, 1, Width - 2, Height - 2);
        using (SolidBrush badgeFill = new SolidBrush(Color.FromArgb(27, 29, 34)))
        {
            graphics.FillEllipse(badgeFill, badge);
        }

        RectangleF globe = new RectangleF(6, 6, Width - 12, Height - 12);
        using (Pen line = new Pen(Color.FromArgb(224, 224, 228), 1.35F))
        {
            graphics.DrawEllipse(line, globe);
            graphics.DrawEllipse(line, globe.X + globe.Width * 0.23F, globe.Y, globe.Width * 0.54F, globe.Height);
            graphics.DrawEllipse(line, globe.X + globe.Width * 0.39F, globe.Y, globe.Width * 0.22F, globe.Height);
            graphics.DrawArc(line, globe.X, globe.Y + globe.Height * 0.20F, globe.Width, globe.Height * 0.60F, 180, 180);
            graphics.DrawArc(line, globe.X, globe.Y + globe.Height * 0.20F, globe.Width, globe.Height * 0.60F, 0, 180);
            graphics.DrawLine(line, globe.Left, globe.Top + globe.Height / 2F, globe.Right, globe.Top + globe.Height / 2F);
        }
    }
}

// ============================================================
// Source: src/PavDPI.Installer/PavDPIProfile.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class PavDPIProfile
{
    private const string DnsArguments = "--dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253";
    private const string TargetArguments = "--blacklist \"..\\config\\targets.txt\"";

    public string Id { get; private set; }
    public string DisplayName { get; private set; }
    public string Description { get; private set; }
    public string Arguments { get; private set; }
    public bool IsAutomatic { get; private set; }

    private PavDPIProfile(string id, string displayName, string description, string arguments, bool isAutomatic)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Arguments = arguments;
        IsAutomatic = isAutomatic;
    }

    public override string ToString()
    {
        return DisplayName;
    }

    public static IList<PavDPIProfile> All()
    {
        return new List<PavDPIProfile>
        {
            new PavDPIProfile(
                "auto",
                "Otomatik",
                "Discord ve Roblox'u dener; calisan en guvenli ayari kendisi secer.",
                String.Empty,
                true),
            Targeted(),
            Balanced(),
            Compatible(),
            Alternative()
        };
    }

    public static IList<PavDPIProfile> AutomaticCandidates()
    {
        return new List<PavDPIProfile> { Targeted(), Balanced(), Compatible(), Alternative() };
    }

    public static PavDPIProfile Find(string id)
    {
        return All().FirstOrDefault(delegate(PavDPIProfile profile)
        {
            return String.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static PavDPIProfile Targeted()
    {
        return new PavDPIProfile(
            "turkey-targeted",
            "Otomatik",
            "TTL 7 yalnizca hedef alan adlarinda kullanilir; genel web trafigi etkilenmez.",
            "-f 2 -e 2 --reverse-frag --max-payload --set-ttl 7 " + TargetArguments + " " + DnsArguments,
            false);
    }

    private static PavDPIProfile Balanced()
    {
        return new PavDPIProfile(
            "turkey-balanced",
            "Otomatik",
            "Modern mod 5 ve otomatik TTL kullanir; yalnizca hedef alan adlarina uygulanir.",
            "-5 " + TargetArguments + " " + DnsArguments,
            false);
    }

    private static PavDPIProfile Compatible()
    {
        return new PavDPIProfile(
            "turkey-compatible",
            "Otomatik",
            "Eski ama uyumlu mod 1; yalnizca hedef alan adlarina uygulanir.",
            "-1 " + TargetArguments + " " + DnsArguments,
            false);
    }

    private static PavDPIProfile Alternative()
    {
        return new PavDPIProfile(
            "turkey-alternative",
            "Otomatik",
            "Alternatif modern mod; yalnizca hedef alan adlarina uygulanir.",
            "-6 " + TargetArguments + " " + DnsArguments,
            false);
    }
}

// ============================================================
// Source: src/PavDPI.Installer/Program.cs
// ============================================================
using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("PavDPI")]
[assembly: AssemblyDescription("PavDPI 2.0 installer and connection manager")]
[assembly: AssemblyCompany("Poroksima")]
[assembly: AssemblyProduct("PavDPI")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Poroksima")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyInformationalVersion("2.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (Mutex singleInstance = new Mutex(true, "Global\\PavDPI.Installer.2", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show(
                    "PavDPI zaten acik. Acik olan pencereyi kullan.",
                    "PavDPI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

// ============================================================
// Source: src/PavDPI.Installer/WindowsServiceManager.cs
// ============================================================
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

internal sealed class ServiceSnapshot
{
    public bool Exists { get; set; }
    public bool WasRunning { get; set; }
    public string BinaryPath { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public string[] Dependencies { get; set; }
    public uint StartType { get; set; }
}

internal static class WindowsServiceManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigFailureActions = 2;
    private const int ServiceConfigFailureActionsFlag = 4;
    private const int ScActionRestart = 1;

    public static string QuoteBinaryPath(string executablePath)
    {
        if (String.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Service executable path is empty.", "executablePath");
        }
        string fullPath = System.IO.Path.GetFullPath(executablePath);
        return "\"" + fullPath.Replace("\"", String.Empty) + "\"";
    }

    public static ServiceSnapshot Capture(string serviceName)
    {
        ServiceSnapshot snapshot = new ServiceSnapshot();
        snapshot.Exists = Exists(serviceName);
        snapshot.WasRunning = snapshot.Exists && IsRunning(serviceName);
        snapshot.StartType = ServiceAutoStart;
        snapshot.Dependencies = new string[0];
        if (!snapshot.Exists) { return snapshot; }

        using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + serviceName, false))
        {
            if (key == null) { return snapshot; }
            snapshot.BinaryPath = Convert.ToString(key.GetValue("ImagePath", String.Empty));
            snapshot.DisplayName = Convert.ToString(key.GetValue("DisplayName", serviceName));
            snapshot.Description = Convert.ToString(key.GetValue("Description", String.Empty));
            snapshot.StartType = Convert.ToUInt32(key.GetValue("Start", (int)ServiceAutoStart));
            object dependencies = key.GetValue("DependOnService", new string[0]);
            snapshot.Dependencies = dependencies as string[] ?? new string[0];
        }
        return snapshot;
    }

    public static void InstallOrUpdate(string serviceName, string displayName, string executablePath, string description)
    {
        IntPtr manager = OpenManager();
        try
        {
            string binaryPath = QuoteBinaryPath(executablePath);
            string dependencies = BuildMultiString(new string[] { "BFE" });
            IntPtr service = OpenService(manager, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceDoesNotExist) { throw new Win32Exception(error, "PavDPI service could not be opened."); }
                service = CreateService(
                    manager,
                    serviceName,
                    displayName,
                    ServiceAllAccess,
                    ServiceWin32OwnProcess,
                    ServiceAutoStart,
                    ServiceErrorNormal,
                    binaryPath,
                    null,
                    IntPtr.Zero,
                    dependencies,
                    null,
                    null);
                if (service == IntPtr.Zero) { ThrowLastError("PavDPI service could not be created."); }
            }
            else
            {
                if (!ChangeServiceConfig(
                    service,
                    ServiceNoChange,
                    ServiceAutoStart,
                    ServiceNoChange,
                    binaryPath,
                    null,
                    IntPtr.Zero,
                    dependencies,
                    null,
                    null,
                    displayName))
                {
                    ThrowLastError("PavDPI service configuration could not be updated.");
                }
            }

            try
            {
                ConfigureDescription(service, description);
                ConfigureRecovery(service);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Restore(string serviceName, ServiceSnapshot snapshot)
    {
        Delete(serviceName);
        if (snapshot == null || !snapshot.Exists) { return; }
        if (String.IsNullOrWhiteSpace(snapshot.BinaryPath))
        {
            throw new InvalidOperationException("Previous service path was empty; rollback cannot recreate it.");
        }

        IntPtr manager = OpenManager();
        try
        {
            IntPtr service = CreateService(
                manager,
                serviceName,
                String.IsNullOrWhiteSpace(snapshot.DisplayName) ? serviceName : snapshot.DisplayName,
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                snapshot.StartType,
                ServiceErrorNormal,
                snapshot.BinaryPath,
                null,
                IntPtr.Zero,
                BuildMultiString(snapshot.Dependencies),
                null,
                null);
            if (service == IntPtr.Zero) { ThrowLastError("Previous PavDPI service could not be restored."); }
            try
            {
                if (!String.IsNullOrWhiteSpace(snapshot.Description)) { ConfigureDescription(service, snapshot.Description); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }

        if (snapshot.WasRunning) { StartAndWait(serviceName, TimeSpan.FromSeconds(20)); }
    }

    public static void StartAndWait(string serviceName, TimeSpan timeout)
    {
        using (ServiceController controller = new ServiceController(serviceName))
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Running) { return; }
            if (controller.Status == ServiceControllerStatus.StopPending)
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                controller.Refresh();
            }
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }
    }

    public static void StopAndWait(string serviceName, TimeSpan timeout)
    {
        if (!Exists(serviceName)) { return; }
        using (ServiceController controller = new ServiceController(serviceName))
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Stopped) { return; }
            if (controller.Status != ServiceControllerStatus.StopPending && controller.CanStop) { controller.Stop(); }
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
    }

    public static void Delete(string serviceName)
    {
        if (!Exists(serviceName)) { return; }
        try { StopAndWait(serviceName, TimeSpan.FromSeconds(20)); } catch { }

        IntPtr manager = OpenManager();
        try
        {
            IntPtr service = OpenService(manager, serviceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist) { return; }
                throw new Win32Exception(error);
            }
            try
            {
                if (!DeleteService(service))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 1072) { throw new Win32Exception(error, "Service could not be deleted."); }
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && Exists(serviceName)) { Thread.Sleep(250); }
    }

    public static bool Exists(string serviceName)
    {
        try
        {
            using (ServiceController controller = new ServiceController(serviceName))
            {
                ServiceControllerStatus ignored = controller.Status;
                return true;
            }
        }
        catch { return false; }
    }

    public static bool IsRunning(string serviceName)
    {
        try
        {
            using (ServiceController controller = new ServiceController(serviceName))
            {
                return controller.Status == ServiceControllerStatus.Running;
            }
        }
        catch { return false; }
    }

    private static IntPtr OpenManager()
    {
        IntPtr manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == IntPtr.Zero) { ThrowLastError("Windows Service Manager could not be opened."); }
        return manager;
    }

    private static void ConfigureDescription(IntPtr service, string description)
    {
        SERVICE_DESCRIPTION value = new SERVICE_DESCRIPTION();
        value.Description = description;
        if (!ChangeServiceConfig2Description(service, ServiceConfigDescription, ref value))
        {
            ThrowLastError("Service description could not be configured.");
        }
    }

    private static void ConfigureRecovery(IntPtr service)
    {
        SC_ACTION[] actions =
        {
            new SC_ACTION { Type = ScActionRestart, Delay = 5000 },
            new SC_ACTION { Type = ScActionRestart, Delay = 15000 },
            new SC_ACTION { Type = ScActionRestart, Delay = 60000 }
        };
        int actionSize = Marshal.SizeOf(typeof(SC_ACTION));
        IntPtr actionPointer = Marshal.AllocHGlobal(actionSize * actions.Length);
        try
        {
            for (int index = 0; index < actions.Length; index++)
            {
                Marshal.StructureToPtr(actions[index], IntPtr.Add(actionPointer, index * actionSize), false);
            }
            SERVICE_FAILURE_ACTIONS value = new SERVICE_FAILURE_ACTIONS();
            value.ResetPeriod = 86400;
            value.ActionCount = (uint)actions.Length;
            value.Actions = actionPointer;
            if (!ChangeServiceConfig2FailureActions(service, ServiceConfigFailureActions, ref value))
            {
                ThrowLastError("Service recovery actions could not be configured.");
            }
            SERVICE_FAILURE_ACTIONS_FLAG flag = new SERVICE_FAILURE_ACTIONS_FLAG();
            flag.Enabled = 1;
            if (!ChangeServiceConfig2FailureFlag(service, ServiceConfigFailureActionsFlag, ref flag))
            {
                ThrowLastError("Service failure flag could not be configured.");
            }
        }
        finally { Marshal.FreeHGlobal(actionPointer); }
    }

    private static string BuildMultiString(string[] values)
    {
        if (values == null || values.Length == 0) { return null; }
        return String.Join("\0", values) + "\0\0";
    }

    private static void ThrowLastError(string message)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION { [MarshalAs(UnmanagedType.LPWStr)] public string Description; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION { public int Type; public uint Delay; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint ResetPeriod;
        public string RebootMessage;
        public string Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS_FLAG { public int Enabled; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(
        IntPtr manager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string loadOrderGroup,
        IntPtr tagId,
        string dependencies,
        string accountName,
        string password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string loadOrderGroup,
        IntPtr tagId,
        string dependencies,
        string accountName,
        string password,
        string displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2Description(IntPtr service, int informationLevel, ref SERVICE_DESCRIPTION information);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2FailureActions(IntPtr service, int informationLevel, ref SERVICE_FAILURE_ACTIONS information);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    private static extern bool ChangeServiceConfig2FailureFlag(IntPtr service, int informationLevel, ref SERVICE_FAILURE_ACTIONS_FLAG information);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr service);
}

// ============================================================
// Source: src/PavDPI.Service/PavDPI.Service.cs
// ============================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: AssemblyTitle("PavDPI Service")]
[assembly: AssemblyDescription("PavDPI Service - background connection engine supervisor")]
[assembly: AssemblyCompany("Poroksima")]
[assembly: AssemblyProduct("PavDPI")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Poroksima")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

internal static class PavDPIServiceProgram
{
    private const string ServiceNameValue = "PavDPI";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return PavDPIEngineSupervisor.RunSelfTest();
            }
            if (args.Length == 2 && String.Equals(args[0], "--run-for", StringComparison.OrdinalIgnoreCase))
            {
                int seconds;
                if (!Int32.TryParse(args[1], out seconds) || seconds < 1 || seconds > 300)
                {
                    Console.Error.WriteLine("--run-for expects 1..300 seconds.");
                    return 4;
                }
                return PavDPIEngineSupervisor.RunForSeconds(seconds);
            }

            ServiceBase.Run(new PavDPIWindowsService());
            return 0;
        }
        catch (Exception exception)
        {
            PavDPILog.Write("Service fatal error: " + exception);
            return 1;
        }
    }

    private sealed class PavDPIWindowsService : ServiceBase
    {
        private PavDPIEngineSupervisor supervisor;

        public PavDPIWindowsService()
        {
            ServiceName = ServiceNameValue;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            RequestAdditionalTime(15000);
            supervisor = new PavDPIEngineSupervisor();
            supervisor.Start();
        }

        protected override void OnStop()
        {
            if (supervisor != null)
            {
                RequestAdditionalTime(15000);
                supervisor.Stop();
                supervisor.Dispose();
                supervisor = null;
            }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }
    }
}

internal sealed class PavDPIEngineSupervisor : IDisposable
{
    private readonly ManualResetEventSlim stopRequested = new ManualResetEventSlim(false);
    private readonly object processLock = new object();
    private readonly PavDPIEngineJob engineJob = new PavDPIEngineJob();
    private Task worker;
    private Process engineProcess;
    private bool disposed;

    public void Start()
    {
        if (worker != null)
        {
            throw new InvalidOperationException("Supervisor is already running.");
        }
        PavDPILog.Write("PavDPI service starting. Version=" + Assembly.GetExecutingAssembly().GetName().Version);
        PavDPIState.Write("STARTING", 0, ReadProfileName(), "Service is starting");
        worker = Task.Factory.StartNew(SuperviseLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        PavDPILog.Write("PavDPI service stop requested.");
        stopRequested.Set();
        StopEngine();
        if (worker != null)
        {
            try
            {
                worker.Wait(12000);
            }
            catch (AggregateException exception)
            {
                PavDPILog.Write("Worker stop wait error: " + exception.Flatten());
            }
        }
        PavDPIState.Write("STOPPED", 0, ReadProfileName(), "Service stopped");
        PavDPILog.Write("PavDPI service stopped.");
    }

    private void SuperviseLoop()
    {
        int rapidExits = 0;
        while (!stopRequested.IsSet)
        {
            Process process = null;
            DateTime startedAt = DateTime.UtcNow;
            try
            {
                process = StartEngine();
                int processId = process.Id;
                string profile = ReadProfileName();
                PavDPIState.Write("ENGINE_RUNNING", processId, profile, "Engine is running");
                PavDPILog.Write("Engine started. PID=" + processId + " Profile=" + profile);

                while (!stopRequested.Wait(500) && !process.HasExited)
                {
                }
                if (stopRequested.IsSet)
                {
                    StopEngine();
                    break;
                }

                int exitCode = process.HasExited ? process.ExitCode : -1;
                TimeSpan uptime = DateTime.UtcNow - startedAt;
                PavDPILog.Write("Engine exited unexpectedly. ExitCode=" + exitCode + " UptimeSeconds=" + (int)uptime.TotalSeconds);
                rapidExits = uptime.TotalSeconds < 20 ? rapidExits + 1 : 0;
            }
            catch (Exception exception)
            {
                rapidExits++;
                PavDPILog.Write("Engine start failure: " + exception);
                PavDPIState.Write("ENGINE_ERROR", 0, ReadProfileName(), exception.Message);
            }
            finally
            {
                lock (processLock)
                {
                    if (engineProcess != null)
                    {
                        try { engineProcess.Dispose(); } catch { }
                        engineProcess = null;
                    }
                }
            }

            if (!stopRequested.IsSet)
            {
                int delaySeconds = Math.Min(60, 3 + (rapidExits * 5));
                PavDPIState.Write("RESTART_WAIT", 0, ReadProfileName(), "Engine restart in " + delaySeconds + " seconds");
                stopRequested.Wait(TimeSpan.FromSeconds(delaySeconds));
            }
        }
    }

    private Process StartEngine()
    {
        string enginePath = Path.Combine(PavDPIPaths.EngineDirectory, "PavDPI Engine.exe");
        ValidateRequiredFile(enginePath, "PavDPI Engine was not found.");
        ValidateRequiredFile(Path.Combine(PavDPIPaths.EngineDirectory, "WinDivert.dll"), "WinDivert.dll was not found.");
        ValidateRequiredFile(Path.Combine(PavDPIPaths.EngineDirectory, "WinDivert64.sys"), "WinDivert64.sys was not found.");
        ValidateRequiredFile(PavDPIPaths.ProfileArgumentsPath, "PavDPI profile arguments were not found.");
        ValidateRequiredFile(PavDPIPaths.TargetsPath, "PavDPI target list was not found.");

        string arguments = File.ReadAllText(PavDPIPaths.ProfileArgumentsPath, Encoding.UTF8).Trim();
        if (String.IsNullOrWhiteSpace(arguments))
        {
            throw new InvalidDataException("PavDPI profile arguments are empty.");
        }

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = enginePath;
        startInfo.Arguments = arguments;
        startInfo.WorkingDirectory = PavDPIPaths.EngineDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        Process process = new Process();
        process.StartInfo = startInfo;
        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
        {
            if (!String.IsNullOrWhiteSpace(eventArgs.Data)) { PavDPILog.Write("engine: " + eventArgs.Data); }
        };
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
        {
            if (!String.IsNullOrWhiteSpace(eventArgs.Data)) { PavDPILog.Write("engine-error: " + eventArgs.Data); }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("PavDPI Engine could not be started.");
        }
        engineJob.TryAssign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (processLock) { engineProcess = process; }
        return process;
    }

    private static void ValidateRequiredFile(string path, string message)
    {
        if (!File.Exists(path)) { throw new FileNotFoundException(message, path); }
    }

    private void StopEngine()
    {
        lock (processLock)
        {
            if (engineProcess == null) { return; }
            try
            {
                if (!engineProcess.HasExited)
                {
                    PavDPILog.Write("Stopping engine PID=" + engineProcess.Id);
                    engineProcess.Kill();
                    engineProcess.WaitForExit(5000);
                }
            }
            catch (Exception exception) { PavDPILog.Write("Engine stop error: " + exception.Message); }
        }
    }

    private static string ReadProfileName()
    {
        try
        {
            return File.Exists(PavDPIPaths.ProfileNamePath)
                ? File.ReadAllText(PavDPIPaths.ProfileNamePath, Encoding.UTF8).Trim()
                : "unknown";
        }
        catch { return "unknown"; }
    }

    public static int RunSelfTest()
    {
        string[] required =
        {
            Path.Combine(PavDPIPaths.EngineDirectory, "PavDPI Engine.exe"),
            Path.Combine(PavDPIPaths.EngineDirectory, "WinDivert.dll"),
            Path.Combine(PavDPIPaths.EngineDirectory, "WinDivert64.sys"),
            PavDPIPaths.ProfileArgumentsPath,
            PavDPIPaths.ProfileNamePath,
            PavDPIPaths.TargetsPath
        };
        foreach (string path in required)
        {
            if (!File.Exists(path)) { Console.Error.WriteLine("Missing: " + path); return 2; }
        }
        string arguments = File.ReadAllText(PavDPIPaths.ProfileArgumentsPath, Encoding.UTF8).Trim();
        if (String.IsNullOrWhiteSpace(arguments)) { Console.Error.WriteLine("Profile arguments are empty."); return 3; }
        if (arguments.Contains("--set-ttl") && !arguments.Contains("--blacklist"))
        {
            Console.Error.WriteLine("Unsafe profile: fixed TTL requires a blacklist.");
            return 5;
        }
        Console.WriteLine("PavDPI Service self-test passed.");
        return 0;
    }

    public static int RunForSeconds(int seconds)
    {
        using (PavDPIEngineSupervisor supervisor = new PavDPIEngineSupervisor())
        {
            supervisor.Start();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            supervisor.Stop();
        }
        return 0;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        engineJob.Dispose();
        stopRequested.Dispose();
    }
}

internal static class PavDPIPaths
{
    public static readonly string InstallDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static readonly string EngineDirectory = Path.Combine(InstallDirectory, "engine");
    public static readonly string ConfigDirectory = Path.Combine(InstallDirectory, "config");
    public static readonly string ProfileArgumentsPath = Path.Combine(ConfigDirectory, "profile.args");
    public static readonly string ProfileNamePath = Path.Combine(ConfigDirectory, "profile.name");
    public static readonly string TargetsPath = Path.Combine(ConfigDirectory, "targets.txt");
    public static readonly string DataDirectory = ResolveDataDirectory();
    public static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
    public static readonly string LogPath = Path.Combine(LogDirectory, "PavDPI.log");
    public static readonly string StatePath = Path.Combine(DataDirectory, "state.txt");

    private static string ResolveDataDirectory()
    {
        string overridePath = Environment.GetEnvironmentVariable("PAVDPI_DATA_DIR");
        return !String.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PavDPI");
    }
}

internal static class PavDPILog
{
    private static readonly object Sync = new object();
    private const long MaxLogBytes = 1024 * 1024;

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(PavDPIPaths.LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(PavDPIPaths.LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(PavDPIPaths.LogPath) || new FileInfo(PavDPIPaths.LogPath).Length < MaxLogBytes) { return; }
        for (int index = 3; index >= 1; index--)
        {
            string source = index == 1 ? PavDPIPaths.LogPath : PavDPIPaths.LogPath + "." + (index - 1);
            string destination = PavDPIPaths.LogPath + "." + index;
            if (File.Exists(destination)) { File.Delete(destination); }
            if (File.Exists(source)) { File.Move(source, destination); }
        }
    }
}

internal static class PavDPIState
{
    private static readonly object Sync = new object();

    public static void Write(string status, int processId, string profile, string detail)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(PavDPIPaths.DataDirectory);
                StringBuilder value = new StringBuilder();
                value.AppendLine("status=" + Clean(status));
                value.AppendLine("pid=" + processId);
                value.AppendLine("profile=" + Clean(profile));
                value.AppendLine("updated=" + DateTime.Now.ToString("o"));
                value.AppendLine("detail=" + Clean(detail));
                string temporary = PavDPIPaths.StatePath + ".tmp";
                File.WriteAllText(temporary, value.ToString(), new UTF8Encoding(false));
                if (File.Exists(PavDPIPaths.StatePath)) { File.Delete(PavDPIPaths.StatePath); }
                File.Move(temporary, PavDPIPaths.StatePath);
            }
            catch (Exception exception) { PavDPILog.Write("State write error: " + exception.Message); }
        }
    }

    private static string Clean(string value)
    {
        return value == null ? String.Empty : value.Replace("\r", " ").Replace("\n", " ");
    }
}

internal sealed class PavDPIEngineJob : IDisposable
{
    private IntPtr handle;

    public PavDPIEngineJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) { return; }
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        information.BasicLimitInformation.LimitFlags = 0x00002000;
        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)length))
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public void TryAssign(Process process)
    {
        if (handle == IntPtr.Zero) { return; }
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            PavDPILog.Write("Engine job assignment skipped. Win32=" + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handleValue);
}
