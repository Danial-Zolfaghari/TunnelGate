using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace TunnelGate.Setup;

internal static class InstallerEngine
{
    private const string ProductName = "TunnelGate";
    private const string ProductVersion = "3.1.12";
    private const string PayloadResource = "TunnelGate.Setup.Payload.TunnelGate.exe";
    private const string RunKey = @"SoftwareMicrosoftWindowsCurrentVersionRun";
    private const string UninstallKey = @"SoftwareMicrosoftWindowsCurrentVersionUninstallTunnelGate";

    internal static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TunnelGate");

    internal static string GetSuggestedInstallDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, false);
            var existing = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(existing))
                return NormalizeInstallDirectory(existing);
        }
        catch { }

        return DefaultInstallDirectory;
    }

    internal static string NormalizeInstallDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Choose an installation folder.");

        var expanded = CleanInstallDirectoryInput(Environment.ExpandEnvironmentVariables(path));
        var full = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The installation path is invalid.");

        var normalized = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a folder inside the drive, not the drive root itself.");

        return normalized;
    }


    private static string CleanInstallDirectoryInput(string rawPath)
    {
        var candidate = rawPath.Trim().Trim('"').Trim();
        if (candidate.Length == 0)
            return candidate;

        // v3.1.11 could persist a malformed value such as:
        // C:...installer-output"C:Users...ProgramsTunnelGate"
        // Recover by using the last absolute Windows drive path in the string.
        var lastDriveRoot = -1;
        for (var i = 0; i <= candidate.Length - 3; i++)
        {
            if (char.IsLetter(candidate[i]) &&
                candidate[i + 1] == ':' &&
                (candidate[i + 2] == '\' || candidate[i + 2] == '/'))
            {
                lastDriveRoot = i;
            }
        }

        if (lastDriveRoot > 0)
            candidate = candidate[lastDriveRoot..];

        return candidate.Trim().Trim('"').Trim();
    }

    internal static void ValidateInstallDirectory(string installDirectory)
    {
        var full = NormalizeInstallDirectory(installDirectory);
        Directory.CreateDirectory(full);

        var probe = Path.Combine(full, $".tunnelgate-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "TunnelGate installer write test");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("TunnelGate cannot write to this folder. Choose a folder your Windows account can write to.");
        }
        finally
        {
            TryDelete(probe);
        }
    }

    internal static void Install(string installDirectory)
    {
        installDirectory = NormalizeInstallDirectory(installDirectory);
        ValidateInstallDirectory(installDirectory);

        var previousDirectory = GetExistingInstallDirectory();
        var installedExe = Path.Combine(installDirectory, "TunnelGate.exe");
        var installedUninstaller = Path.Combine(installDirectory, "TunnelGate.Uninstall.exe");
        var desktopShortcut = GetDesktopShortcut();
        var startMenuDirectory = GetStartMenuDirectory();
        var startMenuShortcut = Path.Combine(startMenuDirectory, "TunnelGate.lnk");
        var startMenuUninstallShortcut = Path.Combine(startMenuDirectory, "Uninstall TunnelGate.lnk");

        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(startMenuDirectory);

        StopInstalledApp(previousDirectory);
        if (!PathsEqual(previousDirectory, installDirectory))
            StopInstalledApp(installDirectory);

        ExtractPayload(installedExe);
        CopySelfAsUninstaller(installedUninstaller);
        File.WriteAllText(Path.Combine(installDirectory, ".tunnelgate-install"), ProductVersion);
        CreateShortcut(desktopShortcut, installedExe, installDirectory, "TunnelGate", installedExe);
        CreateShortcut(startMenuShortcut, installedExe, installDirectory, "TunnelGate", installedExe);
        CreateShortcut(startMenuUninstallShortcut, installedUninstaller, installDirectory, "Uninstall TunnelGate", installedExe, "--uninstall");
        WriteStartupRegistry(installedExe);
        WriteUninstallRegistry(installDirectory, installedExe, installedUninstaller);

        if (!string.IsNullOrWhiteSpace(previousDirectory) && !PathsEqual(previousDirectory, installDirectory))
            TryRemoveOldInstallDirectory(previousDirectory);
    }

    internal static void LaunchInstalledApp(string installDirectory)
    {
        installDirectory = NormalizeInstallDirectory(installDirectory);
        var installedExe = Path.Combine(installDirectory, "TunnelGate.exe");
        if (!File.Exists(installedExe)) return;

        Process.Start(new ProcessStartInfo(installedExe)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory
        });
    }

    internal static void Uninstall()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the uninstaller executable.");
        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(self)) ?? DefaultInstallDirectory;
        var desktopShortcut = GetDesktopShortcut();
        var startMenuDirectory = GetStartMenuDirectory();
        var startMenuShortcut = Path.Combine(startMenuDirectory, "TunnelGate.lnk");
        var startMenuUninstallShortcut = Path.Combine(startMenuDirectory, "Uninstall TunnelGate.lnk");

        try { StopInstalledApp(installDirectory); } catch { }
        try { RemoveStartupRegistry(); } catch { }
        try { RemoveUninstallRegistry(); } catch { }
        TryDelete(desktopShortcut);
        TryDelete(startMenuShortcut);
        TryDelete(startMenuUninstallShortcut);

        try
        {
            if (Directory.Exists(startMenuDirectory) && !Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
                Directory.Delete(startMenuDirectory);
        }
        catch { }

        // Preserve %LOCALAPPDATA%TunnelGate: encrypted vault, profiles and logs are user data.
        ScheduleDirectoryRemoval(installDirectory);
    }

    private static string? GetExistingInstallDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, false);
            var value = key?.GetValue("InstallLocation") as string;
            return string.IsNullOrWhiteSpace(value) ? null : NormalizeInstallDirectory(value);
        }
        catch { return null; }
    }

    private static string GetDesktopShortcut() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TunnelGate.lnk");

    private static string GetStartMenuDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TunnelGate");

    private static void ExtractPayload(string destination)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("Embedded TunnelGate payload was not found.");

        var temp = destination + ".new";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.CopyTo(output);

        File.Move(temp, destination, true);
    }

    private static void CopySelfAsUninstaller(string installedUninstaller)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate setup executable.");
        if (!PathsEqual(self, installedUninstaller))
            File.Copy(self, installedUninstaller, true);
    }

    private static void WriteStartupRegistry(string installedExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.SetValue(ProductName, $""{installedExe}"");
    }

    private static void RemoveStartupRegistry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.DeleteValue(ProductName, false);
    }

    private static void WriteUninstallRegistry(string installDirectory, string installedExe, string installedUninstaller)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true);
        key.SetValue("DisplayName", "TunnelGate");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("DisplayIcon", installedExe);
        key.SetValue("Publisher", "TunnelGate");
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("UninstallString", $""{installedUninstaller}" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void RemoveUninstallRegistry()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description, string iconLocation, string arguments = "")
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable; shortcut creation failed.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        shortcut.IconLocation = $"{iconLocation},0";
        shortcut.Arguments = arguments;
        shortcut.Save();
    }

    private static void StopInstalledApp(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) return;
        var installedExe = Path.Combine(installDirectory, "TunnelGate.exe");

        foreach (var process in Process.GetProcessesByName("TunnelGate"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null || !PathsEqual(path, installedExe))
                    continue;
                process.Kill(true);
                process.WaitForExit(3000);
            }
            catch { }
        }
    }

    private static void TryRemoveOldInstallDirectory(string previousDirectory)
    {
        try
        {
            var full = Path.GetFullPath(previousDirectory);
            if (!Directory.Exists(full)) return;
            TryDelete(Path.Combine(full, "TunnelGate.exe"));
            TryDelete(Path.Combine(full, "TunnelGate.Uninstall.exe"));
            TryDelete(Path.Combine(full, ".tunnelgate-install"));
            if (!Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
        catch { }
    }

    private static void ScheduleDirectoryRemoval(string installDirectory)
    {
        var exe = Path.Combine(installDirectory, "TunnelGate.exe");
        var uninstaller = Path.Combine(installDirectory, "TunnelGate.Uninstall.exe");
        var marker = Path.Combine(installDirectory, ".tunnelgate-install");
        var command = $"/c timeout /t 2 /nobreak >nul & del /f /q "{exe}" 2>nul & del /f /q "{uninstaller}" 2>nul & del /f /q "{marker}" 2>nul & rmdir "{installDirectory}" 2>nul";
        Process.Start(new ProcessStartInfo("cmd.exe", command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\', '/'), Path.GetFullPath(right).TrimEnd('\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
