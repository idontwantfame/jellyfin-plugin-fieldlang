using System.Globalization;
using Jellyfin.Plugin.FieldLang.Configuration;
using Jellyfin.Plugin.FieldLang.Tmdb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FieldLang;

/// <summary>
/// Applies the configured per-field languages to a single item.
/// </summary>
public sealed class FieldLangApplier
{
    private readonly ILibraryManager _libraryManager;
    private readonly TmdbLocalizedClient _tmdb;
    private readonly ILogger<FieldLangApplier> _logger;

    /// <summary>Initializes a new instance of the <see cref="FieldLangApplier"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="tmdb">TMDb reader.</param>
    /// <param name="logger">Logger.</param>
    public FieldLangApplier(ILibraryManager libraryManager, TmdbLocalizedClient tmdb, ILogger<FieldLangApplier> logger)
    {
        _libraryManager = libraryManager;
        _tmdb = tmdb;
        _logger = logger;
    }

    /// <summary>Starts a preview with fresh TMDb data.</summary>
    public void ClearCache() => _tmdb.ClearCache();

    /// <summary>Maps a Jellyfin item to a catalog item type.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The item type name, or null when unsupported.</returns>
    public static string? ResolveItemType(BaseItem item) => item switch
    {
        Movie => FieldCatalog.Movie,
        Series => FieldCatalog.Series,
        Season => FieldCatalog.Season,
        Episode => FieldCatalog.Episode,
        _ => null,
    };

    /// <summary>A non-mutating explanation of what the original-title rule would do.</summary>
    public sealed record OriginalTitlePreview(
        string ItemId,
        string ItemType,
        string CurrentTitle,
        string? ProposedTitle,
        string Reason,
        bool WouldChange);

    /// <summary>
    /// Applies every matching rule to one item and saves it if anything actually changed.
    /// </summary>
    /// <param name="item">The item to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the item was modified and written back.</returns>
    /// <remarks>
    /// Writing only on a real difference keeps repeat sweeps cheap: a second run over an
    /// already-correct library issues no database writes at all.
    /// </remarks>
    public async Task<bool> ApplyAsync(BaseItem item, CancellationToken cancellationToken)
        => await ApplySerializedAsync(item, false, cancellationToken).ConfigureAwait(false);

    /// <summary>Applies only a reviewed original-title rule, leaving all other rules untouched.</summary>
    public Task<bool> ApplyOriginalTitleOnlyAsync(BaseItem item, CancellationToken cancellationToken) =>
        ApplySerializedAsync(item, true, cancellationToken);

    private async Task<bool> ApplySerializedAsync(BaseItem item, bool originalTitleOnly, CancellationToken cancellationToken)
    {
        await Plugin.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(item, originalTitleOnly, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Plugin.MutationGate.Release();
        }
    }

    private async Task<bool> ApplyCoreAsync(BaseItem item, bool originalTitleOnly, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || item.IsLocked || ResolveItemType(item) is not { } itemType)
        {
            return false;
        }

        var rules = ResolveRules(item, itemType, config);
        if (originalTitleOnly)
        {
            rules.RemoveAll(r => r.Field != "OriginalTitle");
        }
        if (rules.Count == 0)
        {
            return false;
        }

        // Finish remote lookups before modifying the shared BaseItem. A user can lock metadata
        // while a request is in flight; no earlier rule should leave a partial in-memory edit.
        var lookupIdentity = LookupIdentity(item);
        var localizedGroups = new List<(List<FieldLanguageRule> Rules, LocalizedFields Fields)>();
        foreach (var group in rules.Where(r => r.Field != "OriginalTitle")
                     .GroupBy(r => r.Language, StringComparer.OrdinalIgnoreCase))
        {
            var localized = await FetchAsync(item, itemType, group.Key, cancellationToken).ConfigureAwait(false);
            if (item.IsLocked || LookupIdentity(item) != lookupIdentity)
            {
                return false;
            }

            if (localized is not null)
            {
                localizedGroups.Add((group.ToList(), localized));
            }
        }

        var changed = false;
        var originalTitleChanged = false;

        var originalTitleRule = rules.FirstOrDefault(r => string.Equals(
            r.Field, "OriginalTitle", StringComparison.Ordinal));
        if (originalTitleRule is not null)
        {
            if (IsApproved(item, itemType, config))
            {
                originalTitleChanged = await ApplyOriginalTitleAsync(item, itemType, config, cancellationToken)
                    .ConfigureAwait(false);
                changed |= originalTitleChanged;
            }
        }

        if (item.IsLocked || LookupIdentity(item) != lookupIdentity)
        {
            return false;
        }

        // Snapshot locks after all lookups, including the original-title lookup. Preserve any
        // unrelated locks added while awaiting TMDb rather than writing an outdated lock array.
        var locked = new HashSet<MetadataField>(item.LockedFields ?? Array.Empty<MetadataField>());
        var lockedBefore = new HashSet<MetadataField>(locked);
        if (originalTitleChanged)
        {
            locked.Add(MetadataField.Name);
        }

        foreach (var group in localizedGroups)
        {
            foreach (var rule in group.Rules)
            {
                if (FieldCatalog.Find(itemType, rule.Field) is not { } field)
                {
                    continue;
                }

                if (field.Apply(group.Fields, item))
                {
                    changed = true;
                    _logger.LogDebug("FieldLang: {Item} {Field} -> {Language}", item.Name, rule.Field, rule.Language);
                }

                if (field.LockField is { } lockField)
                {
                    ApplyLock(rule, lockField, locked);
                }
            }
        }

        if (!locked.SetEquals(lockedBefore))
        {
            item.LockedFields = locked.ToArray();
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        if (originalTitleChanged)
        {
            var backup = FindBackup(config, item);
            if (backup is not null)
            {
                backup.PendingWrite = false;
            }
            Plugin.Instance?.SaveConfiguration();
        }
        return true;
    }

    /// <summary>Previews an active original-title rule without writing Jellyfin metadata.</summary>
    public async Task<OriginalTitlePreview?> PreviewOriginalTitleAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || ResolveItemType(item) is not { } itemType)
        {
            return null;
        }

        var rule = ResolveRules(item, itemType, config).FirstOrDefault(r => string.Equals(
            r.Field, "OriginalTitle", StringComparison.Ordinal));
        if (rule is null)
        {
            return null;
        }

        var backup = FindBackup(config, item);
        if (PreservationReason(item, backup) is { } preservationReason)
        {
            return Preview(item, itemType, null, preservationReason, false);
        }

        var candidate = await GetOriginalTitleAsync(item, itemType, cancellationToken).ConfigureAwait(false);
        if (candidate.Title is null)
        {
            return Preview(item, itemType, null, candidate.Source, false);
        }

        return Preview(
            item,
            itemType,
            candidate.Title,
            string.Equals(item.Name, candidate.Title, StringComparison.Ordinal)
                ? backup is null
                    ? $"already the original title ({candidate.Source}); will record ownership and lock title"
                    : $"already the original title ({candidate.Source})"
                : candidate.Source,
            backup is null || backup.PendingWrite || !string.Equals(item.Name, candidate.Title, StringComparison.Ordinal));
    }

    private List<FieldLanguageRule> ResolveRules(BaseItem item, string itemType, PluginConfiguration config)
    {
        // Match by library id, so renaming a library in Jellyfin does not orphan its rules.
        var libraryIds = _libraryManager.GetCollectionFolders(item)
            .Select(f => f.Id.ToString("N", CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rules = config.Libraries
            .Where(l => libraryIds.Contains(l.LibraryId.Replace("-", string.Empty, StringComparison.Ordinal)))
            .SelectMany(l => l.Rules)
            .ToList();
        return OriginalTitlePolicy.EffectiveRules(rules, itemType);
    }

    private async Task<bool> ApplyOriginalTitleAsync(
        BaseItem item,
        string itemType,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var backup = FindBackup(config, item);
        if (PreservationReason(item, backup) is { } preservationReason)
        {
            if (backup is not null && !backup.ManualOverrideDetected && !backup.PendingRollback && !item.IsLocked)
            {
                backup.ManualOverrideDetected = true;
                Plugin.Instance?.SaveConfiguration();
            }

            _logger.LogInformation("FieldLang: preserving {Item}: {Reason}", item.Id, preservationReason);
            return false;
        }

        var nameBeforeLookup = item.Name;
        var candidate = await GetOriginalTitleAsync(item, itemType, cancellationToken).ConfigureAwait(false);
        if (candidate.Title is null)
        {
            return false;
        }

        // A TMDb lookup can yield while metadata is being edited elsewhere in Jellyfin.
        if (item.Name != nameBeforeLookup || PreservationReason(item, backup) is not null)
        {
            return false;
        }

        var approval = config.OriginalTitleApprovals.FirstOrDefault(a => a.ItemId == item.Id.ToString("N"));
        if (backup is null && !HasScopeApproval(item, itemType, config)
            && (approval is null || approval.CurrentTitle != item.Name || approval.ProposedTitle != candidate.Title))
        {
            return false;
        }

        if (string.Equals(item.Name, candidate.Title, StringComparison.Ordinal))
        {
            if (backup is not null)
            {
                return backup.PendingWrite || !(item.LockedFields ?? Array.Empty<MetadataField>()).Contains(MetadataField.Name);
            }

            // Track and lock already-correct titles too, so later manual edits receive the same
            // protection as titles changed by this rule.
        }

        backup ??= new OriginalTitleBackup
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            OriginalName = item.Name,
            // ForcedSortName is a deliberate user setting. Never replace it; backing it up makes
            // rollback complete even if a later Jellyfin version changes that behavior.
            OriginalForcedSortName = item.ForcedSortName,
            AddedNameLock = !(item.LockedFields ?? Array.Empty<MetadataField>()).Contains(MetadataField.Name),
            LockOwnershipRecorded = true,
            TmdbId = item.GetProviderId(MetadataProvider.Tmdb),
        };
        if (!config.OriginalTitleBackups.Contains(backup))
        {
            config.OriginalTitleBackups.Add(backup);
        }

        if (!backup.PendingWrite)
        {
            // Failed repository writes may have already changed the cached BaseItem. Preserve
            // the last committed title across retries, even if upstream changes its candidate.
            backup.PendingPreviousName = item.Name;
        }
        backup.LastAppliedName = candidate.Title;
        backup.PendingWrite = true;
        // The rollback journal is durable before the server metadata changes. If the process is
        // interrupted between these two operations, the next task can safely retry the pending
        // write and rollback still has the pre-change title.
        Plugin.Instance?.SaveConfiguration();
        item.Name = candidate.Title;
        _logger.LogDebug("FieldLang: {Item} OriginalTitle -> {Source}", item.Id, candidate.Source);
        return true;
    }

    private async Task<(string? Title, string Source)> GetOriginalTitleAsync(
        BaseItem item,
        string itemType,
        CancellationToken cancellationToken)
    {
        // Jellyfin's provider may already have captured this. It is preferable because it is
        // exactly the server's own metadata and does not require a network request.
        if (itemType is not (FieldCatalog.Movie or FieldCatalog.Series))
        {
            return (null, "original titles are not supported for this item type");
        }

        if (OriginalTitlePolicy.IsValidTitle(item.OriginalTitle))
        {
            return (item.OriginalTitle, "Jellyfin OriginalTitle");
        }

        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return (null, "no TMDb provider id");
        }

        var originalTitleBeforeLookup = item.OriginalTitle;
        var candidate = itemType switch
        {
            FieldCatalog.Movie => (await _tmdb.GetMovieOriginalTitleAsync(tmdbId, cancellationToken).ConfigureAwait(false), "TMDb original_title"),
            FieldCatalog.Series => (await _tmdb.GetSeriesOriginalTitleAsync(tmdbId, cancellationToken).ConfigureAwait(false), "TMDb original_name"),
            _ => (null, "original titles are not reliable for this item type"),
        };
        if (tmdbId != item.GetProviderId(MetadataProvider.Tmdb)
            || originalTitleBeforeLookup != item.OriginalTitle)
        {
            return (null, "original-title metadata changed during lookup; run another dry-run");
        }
        return OriginalTitlePolicy.IsValidTitle(candidate.Item1) ? candidate : (null, "no valid original title");
    }

    // Season and episode responses belong to a parent series and numbering, not their own id.
    private static (string? TmdbId, int? Season, int? Episode) LookupIdentity(BaseItem item) => item switch
    {
        Season season => (season.Series?.GetProviderId(MetadataProvider.Tmdb), season.IndexNumber, null),
        Episode episode => (episode.Series?.GetProviderId(MetadataProvider.Tmdb), episode.ParentIndexNumber, episode.IndexNumber),
        _ => (item.GetProviderId(MetadataProvider.Tmdb), null, null),
    };

    private static string? PreservationReason(BaseItem item, OriginalTitleBackup? backup) =>
        OriginalTitlePolicy.PreservationReason(item.Name,
            (item.LockedFields ?? Array.Empty<MetadataField>()).Contains(MetadataField.Name),
            item.IsLocked, item.GetProviderId(MetadataProvider.Tmdb), backup);

    private bool HasScopeApproval(BaseItem item, string itemType, PluginConfiguration config) =>
        _libraryManager.GetCollectionFolders(item).Any(f => config.OriginalTitleApprovedScopes.Contains(
            Plugin.OriginalTitleScope(f.Id.ToString("N"), itemType), StringComparer.Ordinal));

    private bool IsApproved(BaseItem item, string itemType, PluginConfiguration config) =>
        HasScopeApproval(item, itemType, config)
        || config.OriginalTitleApprovals.Any(a => a.ItemId == item.Id.ToString("N")
            && (a.RuleScopes.Count == 0 || _libraryManager.GetCollectionFolders(item).Any(f =>
                a.RuleScopes.Contains(Plugin.OriginalTitleScope(f.Id.ToString("N"), itemType), StringComparer.Ordinal))));

    private static OriginalTitleBackup? FindBackup(PluginConfiguration config, BaseItem item) =>
        config.OriginalTitleBackups.FirstOrDefault(b => string.Equals(
            b.ItemId, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));

    private static OriginalTitlePreview Preview(
        BaseItem item,
        string itemType,
        string? proposed,
        string reason,
        bool wouldChange) =>
        new(item.Id.ToString("N", CultureInfo.InvariantCulture), itemType, item.Name, proposed, reason, wouldChange);

    private static void ApplyLock(FieldLanguageRule rule, MetadataField lockField, HashSet<MetadataField> locked)
    {
        if (rule.Lock)
        {
            locked.Add(lockField);
        }
        else
        {
            // Unticking the box has to actually release the field, otherwise a lock set by an
            // earlier run would stay forever with no way back short of editing the item.
            locked.Remove(lockField);
        }
    }

    private Task<LocalizedFields?> FetchAsync(BaseItem item, string itemType, string language, CancellationToken cancellationToken)
    {
        var none = Task.FromResult<LocalizedFields?>(null);

        switch (itemType)
        {
            case FieldCatalog.Movie when item.GetProviderId(MetadataProvider.Tmdb) is { Length: > 0 } id:
                return _tmdb.GetMovieAsync(id, language, cancellationToken);

            case FieldCatalog.Series when item.GetProviderId(MetadataProvider.Tmdb) is { Length: > 0 } id:
                return _tmdb.GetSeriesAsync(id, language, cancellationToken);

            // Seasons and episodes are addressed through their series, so they need the parent's id
            // plus their own numbering -- a loose episode with no series link cannot be looked up.
            case FieldCatalog.Season when item is Season { IndexNumber: { } season } s
                                          && s.Series?.GetProviderId(MetadataProvider.Tmdb) is { Length: > 0 } seriesId:
                return _tmdb.GetSeasonAsync(seriesId, season, language, cancellationToken);

            case FieldCatalog.Episode when item is Episode { ParentIndexNumber: { } season, IndexNumber: { } number } e
                                           && e.Series?.GetProviderId(MetadataProvider.Tmdb) is { Length: > 0 } seriesId:
                return _tmdb.GetEpisodeAsync(seriesId, season, number, language, cancellationToken);

            default:
                return none;
        }
    }
}
