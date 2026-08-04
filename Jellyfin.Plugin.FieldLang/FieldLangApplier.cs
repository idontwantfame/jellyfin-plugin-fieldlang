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

    /// <summary>
    /// Applies every matching rule to one item and saves it if anything actually changed.
    /// </summary>
    /// <param name="item">The item to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the item was modified and written back.</returns>
    /// <remarks>
    /// Returning false when nothing changed is what stops the ItemUpdated feedback loop: saving
    /// raises the event again, the second pass finds every value already correct, writes nothing,
    /// and the chain ends. No suppression set or re-entrancy flag is needed.
    /// </remarks>
    public async Task<bool> ApplyAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Libraries.Count == 0)
        {
            return false;
        }

        var itemType = ResolveItemType(item);
        if (itemType is null)
        {
            return false;
        }

        var rules = ResolveRules(item, itemType, config);
        if (rules.Count == 0)
        {
            return false;
        }

        // One TMDb call per distinct language, not per rule.
        var byLanguage = rules.GroupBy(r => r.Language, StringComparer.OrdinalIgnoreCase);

        var changed = false;
        var locked = new HashSet<MetadataField>(item.LockedFields ?? Array.Empty<MetadataField>());
        var lockedChanged = false;

        foreach (var group in byLanguage)
        {
            var localized = await FetchAsync(item, itemType, group.Key, cancellationToken).ConfigureAwait(false);
            if (localized is null)
            {
                continue;
            }

            foreach (var rule in group)
            {
                var definition = FieldCatalog.Find(itemType, rule.Field);
                if (definition is null)
                {
                    continue;
                }

                var value = localized.Get(rule.Field);
                if (value is null)
                {
                    // TMDb has no data for this field in this language. Leave whatever the core
                    // provider wrote rather than blanking it.
                    continue;
                }

                if (ApplyValue(item, rule.Field, value))
                {
                    changed = true;
                    _logger.LogDebug(
                        "FieldLang: {Item} {Field} -> {Language}",
                        item.Name,
                        rule.Field,
                        rule.Language);
                }

                if (config.LockAppliedFields && definition.LockField.HasValue && locked.Add(definition.LockField.Value))
                {
                    lockedChanged = true;
                }
            }
        }

        if (lockedChanged)
        {
            item.LockedFields = locked.ToArray();
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

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

    private List<FieldLanguageRule> ResolveRules(BaseItem item, string itemType, PluginConfiguration config)
    {
        // Match by library id, so renaming a library in Jellyfin does not orphan its rules.
        var libraryIds = _libraryManager.GetCollectionFolders(item)
            .Select(f => f.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (libraryIds.Count == 0)
        {
            return new List<FieldLanguageRule>();
        }

        return config.Libraries
            .Where(l => libraryIds.Contains(l.LibraryId))
            .SelectMany(l => l.Rules)
            .Where(r => string.Equals(r.ItemType, itemType, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(r.Language))
            .ToList();
    }

    private async Task<LocalizedFields?> FetchAsync(BaseItem item, string itemType, string language, CancellationToken cancellationToken)
    {
        switch (itemType)
        {
            case FieldCatalog.Movie:
            {
                var id = item.GetProviderId(MetadataProvider.Tmdb);
                return string.IsNullOrEmpty(id)
                    ? null
                    : await _tmdb.GetMovieAsync(id, language, cancellationToken).ConfigureAwait(false);
            }

            case FieldCatalog.Series:
            {
                var id = item.GetProviderId(MetadataProvider.Tmdb);
                return string.IsNullOrEmpty(id)
                    ? null
                    : await _tmdb.GetSeriesAsync(id, language, cancellationToken).ConfigureAwait(false);
            }

            case FieldCatalog.Season:
            {
                if (item is not Season season || season.IndexNumber is null)
                {
                    return null;
                }

                var seriesId = season.Series?.GetProviderId(MetadataProvider.Tmdb);
                return string.IsNullOrEmpty(seriesId)
                    ? null
                    : await _tmdb.GetSeasonAsync(seriesId, season.IndexNumber.Value, language, cancellationToken).ConfigureAwait(false);
            }

            case FieldCatalog.Episode:
            {
                if (item is not Episode episode
                    || episode.ParentIndexNumber is null
                    || episode.IndexNumber is null)
                {
                    return null;
                }

                var seriesId = episode.Series?.GetProviderId(MetadataProvider.Tmdb);
                return string.IsNullOrEmpty(seriesId)
                    ? null
                    : await _tmdb.GetEpisodeAsync(
                        seriesId,
                        episode.ParentIndexNumber.Value,
                        episode.IndexNumber.Value,
                        language,
                        cancellationToken).ConfigureAwait(false);
            }

            default:
                return null;
        }
    }

    private static bool ApplyValue(BaseItem item, string field, object value)
    {
        switch (field)
        {
            case "Name":
                var name = (string)value;
                if (string.Equals(item.Name, name, StringComparison.Ordinal))
                {
                    return false;
                }

                item.Name = name;
                return true;

            case "Overview":
                var overview = (string)value;
                if (string.Equals(item.Overview, overview, StringComparison.Ordinal))
                {
                    return false;
                }

                item.Overview = overview;
                return true;

            case "Tagline":
                var tagline = (string)value;
                if (string.Equals(item.Tagline, tagline, StringComparison.Ordinal))
                {
                    return false;
                }

                item.Tagline = tagline;
                return true;

            case "Genres":
                var genres = (string[])value;
                if (item.Genres is not null && item.Genres.SequenceEqual(genres, StringComparer.Ordinal))
                {
                    return false;
                }

                item.Genres = genres;
                return true;

            default:
                return false;
        }
    }
}
