using System.Net.Mime;
using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.FieldLang.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FieldLang.Api;

/// <summary>
/// Feeds the configuration page the data it needs to render itself.
/// </summary>
/// <remarks>
/// The config page is generic -- it draws a control for every library crossed with every field the
/// catalog supports. Rather than hardcoding that grid in JavaScript, it asks the server, so adding
/// a field to <see cref="FieldCatalog"/> updates the UI with no page edit.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("FieldLang")]
[Produces(MediaTypeNames.Application.Json)]
public class FieldLangController : ControllerBase
{
    private sealed record ReviewedRun(DateTime Created, string Configuration,
        List<FieldLangApplier.OriginalTitlePreview> Items, List<string> Scopes);
    private static readonly ConcurrentDictionary<string, ReviewedRun> Reviews = new();

    /// <summary>A dry-run token and the explicit scope to approve.</summary>
    public sealed class ApprovalRequest
    {
        /// <summary>Gets or sets the token returned by the dry-run.</summary>
        public string Token { get; set; } = string.Empty;
        /// <summary>Gets or sets specific reviewed item ids for a sample run.</summary>
        public List<string> ItemIds { get; set; } = new();
        /// <summary>Gets or sets whether all reviewed scopes and future imports are approved.</summary>
        public bool ApproveLibraries { get; set; }
    }

    private static string ConfigurationFingerprint() => JsonSerializer.Serialize(new
    {
        Plugin.Instance?.Configuration.Libraries,
        Plugin.Instance?.Configuration.TmdbApiKey,
    });
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="FieldLangController"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    public FieldLangController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the libraries and the field catalog.
    /// </summary>
    /// <response code="200">Schema returned.</response>
    /// <returns>The schema the config page renders from.</returns>
    [HttpGet("Schema")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetSchema()
    {
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => new
            {
                Id = f.ItemId,
                Name = f.Name,
                CollectionType = f.CollectionType?.ToString(),
            })
            .ToList();

        var itemTypes = FieldCatalog.ItemTypes
            .Select(t => new
            {
                Name = t,
                Fields = FieldCatalog.ByItemType[t]
                    .Select(f => new { f.Name, f.Label, Lockable = f.LockField.HasValue, f.RequiresLanguage })
                    .ToList(),
            })
            .ToList();

        return Ok(new { Libraries = libraries, ItemTypes = itemTypes });
    }

    /// <summary>Shows every configured movie/series original-title change without writing it.</summary>
    [HttpPost("OriginalTitles/DryRun")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> DryRunOriginalTitles(
        [FromServices] FieldLangApplier applier,
        CancellationToken cancellationToken)
    {
        await Plugin.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            applier.ClearCache();
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true,
            });

            var previews = new List<FieldLangApplier.OriginalTitlePreview>();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preview = await applier.PreviewOriginalTitleAsync(item, cancellationToken).ConfigureAwait(false);
                if (preview is not null)
                {
                    previews.Add(preview);
                }
            }

            var scopes = Plugin.Instance?.Configuration.Libraries.SelectMany(l => l.Rules
                .Where(r => r.Field == "OriginalTitle" && r.ItemType is "Movie" or "Series")
                .Select(r => Plugin.OriginalTitleScope(l.LibraryId, r.ItemType))).Distinct().ToList() ?? new();
            // Tokens are short-lived and held only in memory. A restart or settings change requires
            // another preview; the preview itself never changes metadata or persistent approvals.
            foreach (var old in Reviews.Where(r => DateTime.UtcNow - r.Value.Created > TimeSpan.FromMinutes(30)))
            {
                Reviews.TryRemove(old.Key, out _);
            }
            if (Reviews.Count >= 8)
            {
                Reviews.TryRemove(Reviews.OrderBy(r => r.Value.Created).First().Key, out _);
            }
            var token = Guid.NewGuid().ToString("N");
            Reviews[token] = new(DateTime.UtcNow, ConfigurationFingerprint(), previews, scopes);
            return Ok(new
            {
                Token = token,
                Count = previews.Count,
                WouldChange = previews.Count(p => p.WouldChange),
                Items = previews,
            });
        }
        finally
        {
            Plugin.MutationGate.Release();
        }
    }

    /// <summary>Approves reviewed samples or reviewed library scopes. Samples apply immediately.</summary>
    [HttpPost("OriginalTitles/Approve")]
    public async Task<ActionResult<object>> ApproveOriginalTitles(
        [FromBody] ApprovalRequest request,
        [FromServices] FieldLangApplier applier,
        CancellationToken cancellationToken)
    {
        var sampleIds = new List<Guid>();
        await Plugin.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Reviews.TryGetValue(request.Token, out var review)
                || DateTime.UtcNow - review.Created > TimeSpan.FromMinutes(30)
                || review.Configuration != ConfigurationFingerprint())
            {
                return Conflict("Dry-run expired or settings changed. Run another dry-run.");
            }

            var config = Plugin.Instance!.Configuration;
            if (request.ApproveLibraries)
            {
                if (review.Scopes.Count == 0)
                {
                    return BadRequest("There are no movie/series original-title rules to approve.");
                }
                config.OriginalTitleApprovedScopes = config.OriginalTitleApprovedScopes
                    .Concat(review.Scopes).Distinct().ToList();
            }
            else
            {
                if (request.ItemIds.Count == 0 || request.ItemIds.Count > 50)
                {
                    return BadRequest("Select between 1 and 50 reviewed items for a sample run.");
                }
                var selected = new List<OriginalTitleApproval>();
                foreach (var id in request.ItemIds.Distinct())
                {
                    if (!Guid.TryParse(id, out var itemId)
                        || review.Items.FirstOrDefault(p => p.ItemId == itemId.ToString("N")) is not { ProposedTitle: not null } row
                        || _libraryManager.GetItemById(itemId) is not BaseItem item)
                    {
                        return BadRequest("An item is not eligible in the reviewed dry-run.");
                    }
                    var current = await applier.PreviewOriginalTitleAsync(item, cancellationToken).ConfigureAwait(false);
                    if (current?.CurrentTitle != row.CurrentTitle || current.ProposedTitle != row.ProposedTitle)
                    {
                        return Conflict("An item changed since the dry-run. Run another dry-run.");
                    }
                    selected.Add(new()
                    {
                        ItemId = row.ItemId,
                        CurrentTitle = row.CurrentTitle,
                        ProposedTitle = row.ProposedTitle,
                        RuleScopes = _libraryManager.GetCollectionFolders(item)
                            .Select(f => Plugin.OriginalTitleScope(f.Id.ToString("N"), row.ItemType))
                            .Where(scope => review.Scopes.Contains(scope, StringComparer.Ordinal)).ToList(),
                    });
                    sampleIds.Add(itemId);
                }
                foreach (var approval in selected)
                {
                    config.OriginalTitleApprovals.RemoveAll(a => a.ItemId == approval.ItemId);
                    config.OriginalTitleApprovals.Add(approval);
                }
            }
            Plugin.Instance.SaveConfiguration();
        }
        finally
        {
            Plugin.MutationGate.Release();
        }

        var updated = 0;
        foreach (var id in sampleIds)
        {
            if (_libraryManager.GetItemById(id) is BaseItem item
                && await applier.ApplyOriginalTitleOnlyAsync(item, cancellationToken).ConfigureAwait(false))
            {
                updated++;
            }
        }
        return Ok(new { Updated = updated, LibrariesApproved = request.ApproveLibraries });
    }

    /// <summary>Restores all original-title entries still owned by Field Language.</summary>
    [HttpPost("OriginalTitles/Rollback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RollbackOriginalTitles(CancellationToken cancellationToken)
    {
        await Plugin.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return Ok(new { Restored = 0, Skipped = 0 });
            }

            var restored = 0;
            var skipped = 0;
            // Revoke first so neither the daily task nor a racing sample request can reapply restored
            // titles. Keep skipped backups for a later deliberate manual recovery.
            config.OriginalTitleApprovals.Clear();
            config.OriginalTitleApprovedScopes.Clear();
            foreach (var library in config.Libraries)
            {
                library.Rules.RemoveAll(r => r.Field == "OriginalTitle");
            }
            Reviews.Clear();
            Plugin.Instance?.SaveConfiguration();
            // Copy because successful restores remove journal entries as we iterate.
            foreach (var backup in config.OriginalTitleBackups.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(backup.ItemId, out var itemId)
                    || _libraryManager.GetItemById(itemId) is not BaseItem item
                    || !OriginalTitlePolicy.CanRollback(item.Name, backup, item.IsLocked,
                        (item.LockedFields ?? Array.Empty<MetadataField>()).Contains(MetadataField.Name))
                    || (backup.TmdbId is not null && backup.TmdbId != item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb)))
                {
                    skipped++;
                    continue;
                }

                backup.PendingRollback = true;
                Plugin.Instance?.SaveConfiguration();
                item.Name = backup.OriginalName;
                // We never changed ForcedSortName. Restoring the old snapshot would overwrite a
                // sort title deliberately edited since the task ran, so leave its current value alone.
                if (backup.LockOwnershipRecorded && backup.AddedNameLock)
                {
                    item.LockedFields = (item.LockedFields ?? Array.Empty<MetadataField>())
                        .Where(f => f != MetadataField.Name).ToArray();
                }
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                // Persist each completed restore, including partial runs that are later cancelled.
                config.OriginalTitleBackups.Remove(backup);
                Plugin.Instance?.SaveConfiguration();
                restored++;
            }

            if (restored > 0)
            {
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(new { Restored = restored, Skipped = skipped });
        }
        finally
        {
            Plugin.MutationGate.Release();
        }
    }
}
