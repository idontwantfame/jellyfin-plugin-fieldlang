using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FieldLang.Configuration;
using Jellyfin.Plugin.FieldLang.Tmdb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FieldLang.ScheduledTasks;

/// <summary>
/// Sweeps every library that has rules and applies them.
/// </summary>
/// <remarks>
/// The event hook covers items as they change; this covers everything else -- the initial
/// application after you first configure rules, and repair after a bulk refresh.
/// </remarks>
public class ApplyFieldLanguagesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly FieldLangApplier _applier;
    private readonly TmdbLocalizedClient _tmdb;
    private readonly ILogger<ApplyFieldLanguagesTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="ApplyFieldLanguagesTask"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="applier">Rule applier.</param>
    /// <param name="tmdb">TMDb reader.</param>
    /// <param name="logger">Logger.</param>
    public ApplyFieldLanguagesTask(
        ILibraryManager libraryManager,
        FieldLangApplier applier,
        TmdbLocalizedClient tmdb,
        ILogger<ApplyFieldLanguagesTask> logger)
    {
        _libraryManager = libraryManager;
        _applier = applier;
        _tmdb = tmdb;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Apply per-field metadata languages";

    /// <inheritdoc />
    public string Key => "FieldLangApplyTask";

    /// <inheritdoc />
    public string Description => "Re-applies the configured per-field language rules across all libraries.";

    /// <inheritdoc />
    public string Category => "Field Language";

    /// <inheritdoc />
    /// <remarks>
    /// This task is the plugin's only mechanism, so it has to carry a real default trigger --
    /// with none, a fresh install would sit there doing nothing until someone pressed play by hand.
    /// Daily is the compromise: newly added items keep the core provider's language until the next
    /// run, which is the tradeoff for not hooking every library update. Add more triggers in
    /// Dashboard -> Scheduled Tasks if you want it tighter.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Libraries.Count == 0)
        {
            _logger.LogInformation("FieldLang: no rules configured, nothing to do");
            progress.Report(100);
            return;
        }

        // Start from a clean cache so a manual run picks up upstream TMDb edits.
        _tmdb.ClearCache();

        var kinds = new List<BaseItemKind>();
        var configuredTypes = config.Libraries
            .SelectMany(l => l.Rules)
            .Where(r => !string.IsNullOrWhiteSpace(r.Language))
            .Select(r => r.ItemType)
            .ToHashSet(StringComparer.Ordinal);

        if (configuredTypes.Contains(FieldCatalog.Movie))
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (configuredTypes.Contains(FieldCatalog.Series))
        {
            kinds.Add(BaseItemKind.Series);
        }

        if (configuredTypes.Contains(FieldCatalog.Season))
        {
            kinds.Add(BaseItemKind.Season);
        }

        if (configuredTypes.Contains(FieldCatalog.Episode))
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (kinds.Count == 0)
        {
            _logger.LogInformation("FieldLang: rules exist but no language is set on any of them");
            progress.Report(100);
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            IsVirtualItem = false,
            Recursive = true,
        });

        _logger.LogInformation("FieldLang: sweeping {Count} items", items.Count);

        var processed = 0;
        var updated = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await _applier.ApplyAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FieldLang: failed on {Item}", item.Name);
            }

            processed++;
            progress.Report(processed * 100.0 / items.Count);
        }

        _logger.LogInformation("FieldLang: sweep complete, {Updated} of {Total} items updated", updated, items.Count);
    }
}
