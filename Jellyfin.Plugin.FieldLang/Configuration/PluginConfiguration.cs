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
    /// Gets or sets the language, ISO 639-1 and optionally region-qualified ("pt-BR").
    /// Empty leaves the field alone. The OriginalTitle field does not use a language; its enabled
    /// rule is represented by that field name with an empty language.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this field is pinned against provider writes.
    /// Ignored for fields Jellyfin cannot lock, such as Tagline.
    /// </summary>
    /// <remarks>
    /// Defaults to true, which is also what configs written before this field existed deserialize
    /// to, since XmlSerializer leaves an absent element at its initialized value.
    /// </remarks>
    public bool Lock { get; set; } = true;
}

/// <summary>
/// The title that Field Language replaced for one item, retained for safe rollback and to detect
/// a title subsequently changed by a person.
/// </summary>
public class OriginalTitleBackup
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display title before Field Language first changed it.</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>Gets or sets the explicitly configured sort title before the change, if any.</summary>
    public string? OriginalForcedSortName { get; set; }

    /// <summary>Gets or sets the title most recently written by Field Language.</summary>
    public string LastAppliedName { get; set; } = string.Empty;

    /// <summary>Gets or sets whether a later non-plugin edit was detected.</summary>
    public bool ManualOverrideDetected { get; set; }

    /// <summary>
    /// Gets or sets whether the backup was committed before its matching Jellyfin write finished.
    /// This makes an interrupted task recoverable instead of losing the prior title.
    /// </summary>
    public bool PendingWrite { get; set; }

    /// <summary>Gets or sets the title immediately before a pending write.</summary>
    public string? PendingPreviousName { get; set; }

    /// <summary>Gets or sets whether Field Language added the title lock.</summary>
    public bool AddedNameLock { get; set; }

    /// <summary>Gets or sets whether lock ownership was recorded by the safe implementation.</summary>
    public bool LockOwnershipRecorded { get; set; }

    /// <summary>Gets or sets the provider identity used when the backup was created.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets whether a journaled rollback needs to finish after interruption.</summary>
    public bool PendingRollback { get; set; }
}

/// <summary>An individually reviewed title, approved through a dry-run.</summary>
public class OriginalTitleApproval
{
    /// <summary>Gets or sets the item id.</summary>
    public string ItemId { get; set; } = string.Empty;
    /// <summary>Gets or sets the reviewed display title.</summary>
    public string CurrentTitle { get; set; } = string.Empty;
    /// <summary>Gets or sets the reviewed original title.</summary>
    public string ProposedTitle { get; set; } = string.Empty;
    /// <summary>Gets or sets the library/type rules under which this sample was approved.</summary>
    public List<string> RuleScopes { get; set; } = new();
}

/// <summary>
/// The rules that apply to one library.
/// </summary>
public class LibraryRuleSet
{
    /// <summary>Gets or sets the virtual folder id these rules apply to.</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name, for display only. Matching is by id, so renaming a library
    /// does not break its rules.
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

    /// <summary>Gets or sets the per-library rule sets.</summary>
    public List<LibraryRuleSet> Libraries { get; set; } = new();

    /// <summary>
    /// Gets or sets original-title backups. These are plugin metadata only: no media files or
    /// Jellyfin provider data are changed by keeping this journal.
    /// </summary>
    public List<OriginalTitleBackup> OriginalTitleBackups { get; set; } = new();

    /// <summary>Gets or sets individually reviewed original-title approvals.</summary>
    public List<OriginalTitleApproval> OriginalTitleApprovals { get; set; } = new();

    /// <summary>Gets or sets reviewed library/type scopes, including future imports.</summary>
    public List<string> OriginalTitleApprovedScopes { get; set; } = new();
}
