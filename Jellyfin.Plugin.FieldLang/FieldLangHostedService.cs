using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FieldLang;

/// <summary>
/// Applies field-language rules as items are added or refreshed.
/// </summary>
/// <remarks>
/// This is the load-bearing extension point, and the reason the plugin is not implemented as an
/// <c>IRemoteMetadataProvider</c>. In <c>MetadataService.ExecuteRemoteProviders</c> the merge is
/// <c>if (replaceData || target.X.Length == 0)</c>, so a provider ordered after TMDb overwrites a
/// populated field only when <c>replaceData</c> is true -- that is, only on a manual full refresh.
/// On the scheduled refreshes that do most of the work it would silently do nothing. Reacting to
/// the completed update instead sidesteps provider ordering entirely.
/// </remarks>
public sealed class FieldLangHostedService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly FieldLangApplier _applier;
    private readonly ILogger<FieldLangHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="FieldLangHostedService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="applier">Rule applier.</param>
    /// <param name="logger">Logger.</param>
    public FieldLangHostedService(
        ILibraryManager libraryManager,
        FieldLangApplier applier,
        ILogger<FieldLangHostedService> logger)
    {
        _libraryManager = libraryManager;
        _applier = applier;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.ApplyOnItemUpdate)
        {
            return;
        }

        if (FieldLangApplier.ResolveItemType(e.Item) is null)
        {
            return;
        }

        // The event is raised synchronously on the library thread; do not block it.
        _ = Task.Run(async () =>
        {
            try
            {
                await _applier.ApplyAsync(e.Item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FieldLang: failed to apply rules to {Item}", e.Item.Name);
            }
        });
    }
}
