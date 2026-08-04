using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FieldLang.Configuration;

/// <summary>
/// Describes one field that can be pulled in a per-field language.
/// </summary>
/// <remarks>
/// This catalog is the single place that decides what the plugin can localize. The config page
/// renders itself from it, the applier iterates it, and adding a field means adding one entry
/// here plus a line in the TMDb mapper -- nothing else needs to change.
/// </remarks>
public sealed class LocalizableField
{
    /// <summary>Initializes a new instance of the <see cref="LocalizableField"/> class.</summary>
    /// <param name="name">Stable identifier, persisted in config.</param>
    /// <param name="label">Human label for the config page.</param>
    /// <param name="lockField">The <see cref="MetadataField"/> that pins this field, if one exists.</param>
    public LocalizableField(string name, string label, MetadataField? lockField)
    {
        Name = name;
        Label = label;
        LockField = lockField;
    }

    /// <summary>Gets the stable identifier persisted in configuration.</summary>
    public string Name { get; }

    /// <summary>Gets the human-readable label shown on the config page.</summary>
    public string Label { get; }

    /// <summary>
    /// Gets the metadata field used to pin this value against provider refreshes.
    /// Null when Jellyfin has no lock for it, in which case the plugin can only re-apply after the fact.
    /// </summary>
    public MetadataField? LockField { get; }
}

/// <summary>
/// The set of item types and fields this plugin knows how to localize.
/// </summary>
public static class FieldCatalog
{
    /// <summary>Item type identifier for movies.</summary>
    public const string Movie = "Movie";

    /// <summary>Item type identifier for series.</summary>
    public const string Series = "Series";

    /// <summary>Item type identifier for seasons.</summary>
    public const string Season = "Season";

    /// <summary>Item type identifier for episodes.</summary>
    public const string Episode = "Episode";

    private static readonly LocalizableField _name = new("Name", "Title", MetadataField.Name);
    private static readonly LocalizableField _overview = new("Overview", "Description", MetadataField.Overview);
    private static readonly LocalizableField _tagline = new("Tagline", "Tagline", null);
    private static readonly LocalizableField _genres = new("Genres", "Genres", MetadataField.Genres);

    /// <summary>
    /// Gets the fields supported per item type.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: only fields TMDb actually returns per-language are listed.
    /// Season and episode payloads carry name and overview only -- offering Tagline there would
    /// render a control that could never do anything.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<LocalizableField>> ByItemType { get; } =
        new Dictionary<string, IReadOnlyList<LocalizableField>>(StringComparer.Ordinal)
        {
            [Movie] = new[] { _name, _overview, _tagline, _genres },
            [Series] = new[] { _name, _overview, _tagline, _genres },
            [Season] = new[] { _name, _overview },
            [Episode] = new[] { _name, _overview },
        };

    /// <summary>Gets every item type the plugin handles, in display order.</summary>
    public static IReadOnlyList<string> ItemTypes { get; } = new[] { Movie, Series, Season, Episode };

    /// <summary>Looks up a field definition for an item type.</summary>
    /// <param name="itemType">The item type.</param>
    /// <param name="fieldName">The field name.</param>
    /// <returns>The field, or null when unsupported.</returns>
    public static LocalizableField? Find(string itemType, string fieldName)
    {
        if (!ByItemType.TryGetValue(itemType, out var fields))
        {
            return null;
        }

        return fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal));
    }
}
