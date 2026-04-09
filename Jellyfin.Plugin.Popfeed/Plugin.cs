using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        TryDeleteStalePluginDirectories();
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

    private void TryDeleteStalePluginDirectories()
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

            foreach (var directory in pluginsRoot.EnumerateDirectories("Popfeed_*"))
            {
                if (string.Equals(directory.FullName, currentDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    directory.Delete(true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
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