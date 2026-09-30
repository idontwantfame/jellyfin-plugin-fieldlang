using System.Globalization;
using Jellyfin.Plugin.FieldLang.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FieldLang;

/// <summary>
/// Per-field metadata language plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    internal static SemaphoreSlim MutationGate { get; } = new(1, 1);

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        MutationGate.Wait();
        try
        {
            var incoming = (PluginConfiguration)configuration;
            var previousScopes = GetOriginalTitleScopes(Configuration);
            var enabledScopes = GetOriginalTitleScopes(incoming);
            // Journal and approvals belong to the server. A dashboard save may be based on an
            // older config response and must not erase backups created by the running task.
            incoming.OriginalTitleBackups = Configuration.OriginalTitleBackups;
            incoming.OriginalTitleApprovals = Configuration.OriginalTitleApprovals
                .Where(a => a.RuleScopes.Count == 0
                    ? previousScopes.SetEquals(enabledScopes)
                    : a.RuleScopes.Any(enabledScopes.Contains))
                .Select(a => new OriginalTitleApproval
                {
                    ItemId = a.ItemId,
                    CurrentTitle = a.CurrentTitle,
                    ProposedTitle = a.ProposedTitle,
                    RuleScopes = a.RuleScopes.Where(enabledScopes.Contains).ToList(),
                })
                .ToList();
            incoming.OriginalTitleApprovedScopes = Configuration.OriginalTitleApprovedScopes
                .Where(enabledScopes.Contains)
                .ToList();
            foreach (var library in incoming.Libraries)
            {
                foreach (var type in new[] { "Movie", "Series" })
                {
                    if (library.Rules.Any(r => r.ItemType == type && r.Field == "OriginalTitle"))
                    {
                        library.Rules.RemoveAll(r => r.ItemType == type && r.Field == "Name");
                        foreach (var rule in library.Rules.Where(r => r.ItemType == type && r.Field == "OriginalTitle"))
                        {
                            rule.Lock = true;
                        }
                    }
                }
            }
            base.UpdateConfiguration(incoming);
        }
        finally
        {
            MutationGate.Release();
        }
    }

    internal static string OriginalTitleScope(string libraryId, string itemType) =>
        libraryId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() + ":" + itemType;

    private static HashSet<string> GetOriginalTitleScopes(PluginConfiguration config) => config.Libraries
        .SelectMany(l => l.Rules.Where(r => r.Field == "OriginalTitle" && r.ItemType is "Movie" or "Series")
            .Select(r => OriginalTitleScope(l.LibraryId, r.ItemType)))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the singleton instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Field Language";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6b2f4a3c-9d51-4f27-8a6e-1c0d7b5e93a4");

    /// <inheritdoc />
    public override string Description =>
        "Pull individual metadata fields in different languages -- English titles with Turkish descriptions, or any other combination, per library.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}
