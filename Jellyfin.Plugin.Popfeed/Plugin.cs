using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Popfeed.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Popfeed;

/// <summary>
/// The main plugin class.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The xml serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        EnsureConfigurationDefaults();
        TryDeleteOrScheduleStalePluginDirectories();
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <inheritdoc />
    public override string Name => "Popfeed";

    /// <inheritdoc />
    public override string Description => "Sync Jellyfin watched and unwatched actions to Popfeed over ATProto.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("14af54bf-b9d9-4c8a-8c16-a8e993f8ec08");

    private void EnsureConfigurationDefaults()
    {
        if (string.IsNullOrWhiteSpace(Configuration.WatchStateProvider))
        {
            Configuration.WatchStateProvider = PluginConfiguration.PopfeedWatchedListProviderName;
            SaveConfiguration();
        }
    }

    private void TryDeleteOrScheduleStalePluginDirectories()
    {
        try
        {
            var currentDirectory = Path.GetDirectoryName(GetType().Assembly.Location);
            if (string.IsNullOrWhiteSpace(currentDirectory))
            {
                return;
            }

            var pluginsRoot = Directory.GetParent(currentDirectory);
            if (pluginsRoot is null)
            {
                return;
            }

            var directoryPrefixes = GetPluginDirectoryPrefixes();
            var currentVersion = GetCurrentPluginVersion(
                currentDirectory,
                directoryPrefixes);
            var candidateDirectories = pluginsRoot
                .EnumerateDirectories()
                .Where(directory => IsVersionedPluginDirectory(directory.Name, directoryPrefixes))
                .ToArray();

            var parsedVersions = candidateDirectories
                .Select(directory => new PluginDirectoryVersion(directory, TryParseDirectoryVersion(directory.Name, directoryPrefixes)))
                .Where(entry => entry.Version is not null)
                .ToArray();

            if (parsedVersions.Length == 0)
            {
                return;
            }

            var highestInstalledVersion = parsedVersions.Max(entry => entry.Version)!;

            var remainingDirectories = new List<string>();
            foreach (var directory in candidateDirectories)
            {
                var shouldDeleteCurrentDirectory = string.Equals(directory.FullName, currentDirectory, StringComparison.OrdinalIgnoreCase)
                    && currentVersion < highestInstalledVersion;

                if (!shouldDeleteCurrentDirectory && !ShouldDeleteDirectory(directory.Name, directoryPrefixes, highestInstalledVersion))
                {
                    continue;
                }

                if (!TryDeleteDirectory(directory.FullName))
                {
                    remainingDirectories.Add(directory.FullName);
                }
            }

            if (remainingDirectories.Count > 0)
            {
                ScheduleDeferredCleanup(remainingDirectories);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Version GetCurrentPluginVersion(
        string currentDirectory,
        IReadOnlyCollection<string> prefixes)
    {
        var directoryName = Path.GetFileName(currentDirectory);
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var parsedDirectoryVersion = TryParseDirectoryVersion(
                directoryName,
                prefixes);
            if (parsedDirectoryVersion is not null)
            {
                return parsedDirectoryVersion;
            }
        }

        return GetType().Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private string[] GetPluginDirectoryPrefixes()
    {
        var assemblyName = GetType().Assembly.GetName().Name ?? Name;

        return new[]
        {
            Name,
            assemblyName,
            assemblyName.Replace(".", "-", StringComparison.Ordinal),
            assemblyName.Replace(".", "_", StringComparison.Ordinal),
            "jellyfin-plugin-" + Name,
        }
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Version? TryParseDirectoryVersion(string directoryName, IReadOnlyCollection<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || directoryName.Length <= prefix.Length)
            {
                continue;
            }

            var delimiter = directoryName[prefix.Length];
            if (delimiter != '_' && delimiter != '-')
            {
                continue;
            }

            var versionSuffix = directoryName[(prefix.Length + 1)..];
            var versionText = ExtractVersionPrefix(versionSuffix);
            if (string.IsNullOrWhiteSpace(versionText)
                || !Version.TryParse(versionText, out var directoryVersion))
            {
                return null;
            }

            return directoryVersion;
        }

        return null;
    }

    private static bool IsVersionedPluginDirectory(string directoryName, IReadOnlyCollection<string> prefixes)
    {
        return prefixes.Any(prefix =>
            directoryName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
            || directoryName.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractVersionPrefix(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = Regex.Match(input, @"^\d+(?:\.\d+){0,3}");
        return match.Success
            ? match.Value
            : null;
    }

    private static bool ShouldDeleteDirectory(string directoryName, IReadOnlyCollection<string> prefixes, Version highestInstalledVersion)
    {
        var directoryVersion = TryParseDirectoryVersion(directoryName, prefixes);
        if (directoryVersion is null)
        {
            return false;
        }

        if (directoryVersion < highestInstalledVersion)
        {
            return true;
        }

        return false;
    }

    private sealed record PluginDirectoryVersion(DirectoryInfo Directory, Version? Version);

    private static bool TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, true);
            }

            return !Directory.Exists(directoryPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ScheduleDeferredCleanup(IReadOnlyCollection<string> directoryPaths)
    {
        if (directoryPaths.Count == 0)
        {
            return;
        }

        try
        {
            var processId = Process.GetCurrentProcess().Id;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                StartWindowsCleanupProcess(processId, directoryPaths);
                return;
            }

            StartPosixCleanupProcess(processId, directoryPaths);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void StartWindowsCleanupProcess(int processId, IReadOnlyCollection<string> directoryPaths)
    {
        var paths = string.Join(", ", directoryPaths.Select(ToPowerShellSingleQuotedLiteral));
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "$targetPid={0}; try {{ Wait-Process -Id $targetPid -ErrorAction SilentlyContinue }} catch {{ }}; $paths=@({1}); foreach ($path in $paths) {{ if (Test-Path -LiteralPath $path) {{ Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }} }}",
            processId,
            paths);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = Process.Start(startInfo);
    }

    private static void StartPosixCleanupProcess(int processId, IReadOnlyCollection<string> directoryPaths)
    {
        var quotedPaths = string.Join(" ", directoryPaths.Select(ToPosixSingleQuotedLiteral));
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "while kill -0 {0} 2>/dev/null; do sleep 1; done; rm -rf {1}",
            processId,
            quotedPaths);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c " + ToPosixSingleQuotedLiteral(command),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = Process.Start(startInfo);
    }

    private static string ToPowerShellSingleQuotedLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string ToPosixSingleQuotedLiteral(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}