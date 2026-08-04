using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FieldLang.Configuration;

/// <summary>
/// One field pinned to one language, within a library.
/// </summary>
public class FieldLanguageRule
{
    /// <summary>Gets or sets the item type (see <see cref="FieldCatalog"/>).</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the field name (see <see cref="FieldCatalog"/>).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO 639-1 language to pull this field in, optionally region-qualified (for example "pt-BR").
    /// Empty means "leave alone" -- the plugin will not touch the field at all.
    /// </summary>
    public string Language { get; set; } = string.Empty;
}

/// <summary>
/// The rules that apply to one library.
/// </summary>
public class LibraryRuleSet
{
    /// <summary>
    /// Gets or sets the library (virtual folder) item id these rules apply to.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name at the time the rule was saved. Display only -- matching is by id,
    /// so renaming a library in Jellyfin does not break the rule.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-field rules for this library.</summary>
    public List<FieldLanguageRule> Rules { get; set; } = new();
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDb API key (v3). Required -- the server's built-in key is compiled into
    /// the core assembly and is not reachable from a plugin.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an applied field is also locked against future provider writes.
    /// </summary>
    /// <remarks>
    /// On by default. Without it the core provider rewrites the field on every refresh and the plugin
    /// puts it back afterwards, which works but leaves a visible window of the wrong language. Fields
    /// with no corresponding <see cref="MediaBrowser.Model.Entities.MetadataField"/> (Tagline) cannot be
    /// locked and always rely on re-application.
    /// </remarks>
    public bool LockAppliedFields { get; set; } = true;

    /// <summary>Gets or sets the per-library rule sets.</summary>
    public List<LibraryRuleSet> Libraries { get; set; } = new();
}
