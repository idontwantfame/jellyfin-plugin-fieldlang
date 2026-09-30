using System.Reflection;
using System.Net;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.FieldLang;
using Jellyfin.Plugin.FieldLang.Api;
using Jellyfin.Plugin.FieldLang.Configuration;
using Jellyfin.Plugin.FieldLang.Tmdb;
using Jellyfin.Plugin.FieldLang.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
BaseItem.ConfigurationManager = Stub<IServerConfigurationManager>.Create((method, args) =>
    method.Name == "get_Configuration" ? new ServerConfiguration() : null);
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    passed++;
}

var config = new PluginConfiguration();
var serialized = new List<string>();
using var workspace = new TestWorkspace();
var paths = Stub<IApplicationPaths>.Create((method, args) => method.Name.StartsWith("get_") ? workspace.Path : null);
var serializer = Stub<IXmlSerializer>.Create((method, args) =>
{
    if (method.Name == "DeserializeFromFile")
    {
        var file = (string)args![1]!;
        if (!File.Exists(file)) return config;
        using var input = File.OpenRead(file);
        return new XmlSerializer((Type)args[0]!).Deserialize(input);
    }
    if (method.Name == "SerializeToFile")
    {
        var file = (string)args![1]!;
        if (!file.StartsWith(workspace.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new Exception("Test configuration must stay in its own temporary directory");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        using var output = File.Create(file);
        new XmlSerializer(args[0]!.GetType()).Serialize(output, args[0]);
        serialized.Add(System.Text.Json.JsonSerializer.Serialize(args[0]));
    }
    return null;
});
var plugin = new Plugin(paths, serializer);
plugin.UpdateConfiguration(config);
config = plugin.Configuration;
var folder = new CollectionFolder { Id = Guid.NewGuid() };
var items = new List<BaseItem>();
var library = Stub<ILibraryManager>.Create((method, args) => method.Name switch
{
    "GetCollectionFolders" => new List<Folder> { folder },
    "GetItemList" => items,
    "GetItemById" => items.FirstOrDefault(i => i.Id == (Guid)args![0]!),
    _ => null,
});
var handler = new TmdbHandler();
var tmdb = new TmdbLocalizedClient(new ClientFactory(handler), NullLogger<TmdbLocalizedClient>.Instance);
var applier = new FieldLangApplier(library, tmdb, NullLogger<FieldLangApplier>.Instance);
var controller = new FieldLangController(library);
config.TmdbApiKey = "test-key";
config.Libraries.Add(new() { LibraryId = folder.Id.ToString("N"), Rules = new()
{
    new() { ItemType = "Movie", Field = "OriginalTitle", Lock = false },
    new() { ItemType = "Movie", Field = "Name", Language = "en" },
    new() { ItemType = "Movie", Field = "Overview", Language = "en" },
} });

var polish = new TestMovie { Id = Guid.NewGuid(), Name = "English translation", OriginalTitle = "Zażółć gęślą jaźń", Overview = "Unchanged description", ForcedSortName = "My custom sorting" };
polish.SetProviderId(MetadataProvider.Tmdb, "333");
items.Add(polish);
Check(!await applier.ApplyOriginalTitleOnlyAsync(polish, default) && polish.Writes == 0 && polish.LockedFields.Length == 0,
    "Unapproved rules must not change titles or locks");

// Samples use only the original-title rule even with unrelated field rules configured.
var dryRun = (OkObjectResult)(await controller.DryRunOriginalTitles(applier, default)).Result!;
var json = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Value);
var token = json.GetProperty("Token").GetString()!;
Check(polish.Writes == 0 && config.OriginalTitleBackups.Count == 0, "Dry-run must be read-only");
await controller.ApproveOriginalTitles(new() { Token = token, ItemIds = new() { polish.Id.ToString("N") } }, applier, default);
Check(polish.Name == polish.OriginalTitle && polish.Writes == 1, "Selected sample should use its original title");
Check(polish.Overview == "Unchanged description" && polish.ForcedSortName == "My custom sorting", "Samples must preserve description and manual sort title");
Check(polish.LockedFields.Contains(MetadataField.Name), "Original title must be locked even with legacy Lock=false");
Check(serialized.Any(s => s.Contains("English translation") && s.Contains("\"PendingWrite\":true")), "Backup must be saved before title update");
Check(File.ReadAllText(plugin.ConfigurationFilePath).Contains("Zażółć gęślą jaźń"), "XML configuration must preserve Polish title text");
plugin = new Plugin(paths, serializer);
config = plugin.Configuration;
Check(config.OriginalTitleBackups.Single().LastAppliedName == polish.Name
    && config.OriginalTitleApprovals.Single().RuleScopes.Count == 1,
    "Restart must restore XML-backed journal and sample approval scopes");
var unchanged = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
unchanged.OriginalTitleApprovals.Clear();
plugin.UpdateConfiguration(unchanged);
config = plugin.Configuration;
Check(config.OriginalTitleApprovals.Count == 1, "Unchanged dashboard saves must preserve server-owned sample approvals");
unchanged = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
unchanged.Libraries[0].LibraryName = "Renamed library";
unchanged.TmdbApiKey = "another-test-key";
unchanged.Libraries[0].Rules.First(r => r.Field == "Overview").Language = "pl";
plugin.UpdateConfiguration(unchanged);
config = plugin.Configuration;
Check(config.OriginalTitleApprovals.Count == 1, "Library renames, API key changes, and unrelated field rules must preserve sample approvals");
Check(!await applier.ApplyOriginalTitleOnlyAsync(polish, default) && polish.Writes == 1, "Repeat application must be idempotent");

polish.Name = "My deliberate title";
Check(!await applier.ApplyOriginalTitleOnlyAsync(polish, default) && polish.Name == "My deliberate title", "Later manual title must be preserved");
Check((await applier.PreviewOriginalTitleAsync(polish, default))!.ProposedTitle is null, "Preview must match manual-edit preservation");

var manual = new TestMovie { Id = Guid.NewGuid(), Name = "Existing custom title", OriginalTitle = "Provider original", LockedFields = new[] { MetadataField.Name } };
items.Add(manual);
Check((await applier.PreviewOriginalTitleAsync(manual, default))!.ProposedTitle is null, "Existing manual title locks must be skipped");

// Library approval permits new imports but still respects manual locks.
dryRun = (OkObjectResult)(await controller.DryRunOriginalTitles(applier, default)).Result!;
token = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Value).GetProperty("Token").GetString()!;
await controller.ApproveOriginalTitles(new() { Token = token, ApproveLibraries = true }, applier, default);
Check(!await applier.ApplyOriginalTitleOnlyAsync(manual, default), "Library approval must not override manual locks");
var english = new TestMovie { Id = Guid.NewGuid(), Name = "English original", OriginalTitle = "English original" };
items.Add(english);
Check((await applier.PreviewOriginalTitleAsync(english, default))!.WouldChange, "Preview must disclose a lock-only metadata change");
Check(await applier.ApplyOriginalTitleOnlyAsync(english, default) && english.Name == "English original", "Already-correct imported titles must be tracked and locked");
Check(!await applier.ApplyOriginalTitleOnlyAsync(english, default) && english.Writes == 1, "Already-correct titles must be idempotent after locking");
var japanese = new TestMovie { Id = Guid.NewGuid(), Name = "Translation", OriginalTitle = "\0invalid" };
japanese.SortName = "translation";
japanese.SetProviderId(MetadataProvider.Tmdb, "123");
items.Add(japanese);
Check(await applier.ApplyOriginalTitleOnlyAsync(japanese, default) && japanese.Name == "日本語の原題", "Invalid local original title must fall back to TMDb original_title");
Check(japanese.SortName != "translation", "Changing display title must invalidate cached automatic sorting");
japanese.ForcedSortName = "Later manual sort";

var interrupted = new TestMovie { Id = Guid.NewGuid(), Name = "Before", OriginalTitle = "After", FailNextWrite = true };
items.Add(interrupted);
try { await applier.ApplyOriginalTitleOnlyAsync(interrupted, default); } catch (IOException) { }
Check(config.OriginalTitleBackups.Single(b => b.ItemId == interrupted.Id.ToString("N")).PendingWrite, "Failed metadata write must retain its recovery journal");
plugin = new Plugin(paths, serializer);
config = plugin.Configuration;
Check(config.OriginalTitleBackups.Single(b => b.ItemId == interrupted.Id.ToString("N")).PendingWrite,
    "Pending writes must survive a real XML-backed plugin restart");
interrupted.OriginalTitle = "Updated upstream original";
interrupted.FailNextWrite = true;
try { await applier.ApplyOriginalTitleOnlyAsync(interrupted, default); } catch (IOException) { }
Check(config.OriginalTitleBackups.Single(b => b.ItemId == interrupted.Id.ToString("N")).PendingPreviousName == "Before",
    "Repeated failed writes must not replace the last committed title with a cached uncommitted title");
// Simulate reloading the unchanged database row after a restart.
interrupted.Name = "Before";
interrupted.LockedFields = Array.Empty<MetadataField>();
Check(await applier.ApplyOriginalTitleOnlyAsync(interrupted, default), "Interrupted write must be retryable");

config.Libraries[0].Rules.Add(new() { ItemType = "Series", Field = "OriginalTitle" });
config.Libraries[0].Rules.Add(new() { ItemType = "Series", Field = "Name", Language = "en" });
var series = new TestSeries { Id = Guid.NewGuid(), Name = "Series translation" };
series.SetProviderId(MetadataProvider.Tmdb, "456");
items.Add(series);
dryRun = (OkObjectResult)(await controller.DryRunOriginalTitles(applier, default)).Result!;
token = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Value).GetProperty("Token").GetString()!;
await controller.ApproveOriginalTitles(new() { Token = token, ApproveLibraries = true }, applier, default);
Check(await applier.ApplyAsync(series, default) && series.Name == "Série originale", "Series must use original_name and suppress conflicting localized titles");
Check(!await applier.ApplyAsync(series, default) && series.Writes == 1, "Conflicting title rules must not cause repeat writes");

var lockedLater = new TestMovie { Id = Guid.NewGuid(), Name = "Before lock", OriginalTitle = "Managed title" };
items.Add(lockedLater);
await applier.ApplyOriginalTitleOnlyAsync(lockedLater, default);
lockedLater.IsLocked = true;
lockedLater.OriginalTitle = "Upstream changed title";
lockedLater.Overview = "Manually locked description";
lockedLater.SetProviderId(MetadataProvider.Tmdb, "987");
Check((await applier.PreviewOriginalTitleAsync(lockedLater, default))!.ProposedTitle is null,
    "Preview must respect a full item lock added after initial management");
Check(!await applier.ApplyOriginalTitleOnlyAsync(lockedLater, default)
    && lockedLater.Name == "Managed title" && lockedLater.Writes == 1,
    "Application must not overwrite a managed item that is now fully locked");
Check(!await applier.ApplyAsync(lockedLater, default) && lockedLater.Overview == "Manually locked description",
    "Full item locks must block every configured field rule, including descriptions");

var missing = new TestMovie { Id = Guid.NewGuid(), Name = "Keep existing title" };
missing.SetProviderId(MetadataProvider.Tmdb, "404");
items.Add(missing);
Check(!await applier.ApplyOriginalTitleOnlyAsync(missing, default)
    && missing.Name == "Keep existing title" && missing.LockedFields.Length == 0,
    "Missing original titles must preserve the display title without locking it");

// Removing the title lock relinquishes ownership, even before another application detects it.
var unlockedTitle = new TestMovie { Id = Guid.NewGuid(), Name = "Before unlocking", OriginalTitle = "Original unlocked title" };
items.Add(unlockedTitle);
await applier.ApplyOriginalTitleOnlyAsync(unlockedTitle, default);
unlockedTitle.LockedFields = Array.Empty<MetadataField>();

// Metadata APIs may send a stale copy of server-owned state along with edited settings.
var incoming = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
incoming.OriginalTitleBackups.Clear();
incoming.OriginalTitleApprovedScopes.Clear();
plugin.UpdateConfiguration(incoming);
config = plugin.Configuration;
Check(config.OriginalTitleBackups.Count > 0 && config.OriginalTitleApprovedScopes.Count > 0,
    "Stale dashboard saves must preserve journal and library approvals");
Check(!config.Libraries[0].Rules.Any(r => r.Field == "Name"), "Saved original-title rules must remove conflicting localized title rules");
Check(config.Libraries[0].Rules.Where(r => r.Field == "OriginalTitle").All(r => r.Lock), "Saved original-title rules must enforce lock protection");
var staleApproval = await controller.ApproveOriginalTitles(new() { Token = token, ApproveLibraries = true }, applier, default);
Check(staleApproval.Result is ConflictObjectResult, "Settings changes must invalidate reviewed tokens");

// An interrupted rollback can safely resume without mistaking its own restored title for an edit.
japanese.FailNextWrite = true;
try { await controller.RollbackOriginalTitles(default); } catch (IOException) { }
Check(config.OriginalTitleBackups.Single(b => b.ItemId == japanese.Id.ToString("N")).PendingRollback,
    "Interrupted rollback must retain a durable recovery marker");
plugin = new Plugin(paths, serializer);
config = plugin.Configuration;
Check(config.OriginalTitleBackups.Single(b => b.ItemId == japanese.Id.ToString("N")).PendingRollback,
    "Pending rollback must survive a real XML-backed plugin restart");

await controller.RollbackOriginalTitles(default);
Check(japanese.Name == "Translation" && japanese.ForcedSortName == "Later manual sort", "Rollback must restore display title and preserve subsequent manual sorting");
Check(!japanese.LockedFields.Contains(MetadataField.Name), "Rollback must remove only the title lock it added");
Check(manual.LockedFields.Contains(MetadataField.Name) && polish.Name == "My deliberate title", "Rollback must preserve manual edits and pre-existing locks");
Check(!await applier.ApplyOriginalTitleOnlyAsync(japanese, default) && config.OriginalTitleApprovedScopes.Count == 0, "Rollback must prevent the next task reapplying titles");
Check(lockedLater.Name == "Managed title" && lockedLater.Writes == 1
    && lockedLater.LockedFields.Contains(MetadataField.Name),
    "Rollback must preserve titles and locks of fully locked items");
Check(config.OriginalTitleBackups.Any(b => b.ItemId == lockedLater.Id.ToString("N")),
    "Rollback must retain the backup for an item skipped because it is fully locked");
Check(unlockedTitle.Name == "Original unlocked title" && unlockedTitle.Writes == 1
    && config.OriginalTitleBackups.Any(b => b.ItemId == unlockedTitle.Id.ToString("N")),
    "Rollback must preserve a title whose lock was removed without requiring an intervening task run");
unlockedTitle.LockedFields = new[] { MetadataField.Name };
lockedLater.IsLocked = false;
await controller.RollbackOriginalTitles(default);
Check(lockedLater.Name == "Before lock" && !lockedLater.LockedFields.Contains(MetadataField.Name),
    "Unlocking a previously skipped item must allow a later deliberate rollback");
Check(unlockedTitle.Name == "Before unlocking" && unlockedTitle.LockedFields.Length == 0,
    "Restoring title protection must allow a deliberate rollback of the retained backup");

// Approvals survive unrelated settings changes, but removing their title rule revokes them.
config.Libraries[0].Rules.Add(new() { ItemType = "Movie", Field = "OriginalTitle" });
var reapprove = new TestMovie { Id = Guid.NewGuid(), Name = "Translation", OriginalTitle = "Unknown" };
items.Add(reapprove);
dryRun = (OkObjectResult)(await controller.DryRunOriginalTitles(applier, default)).Result!;
token = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Value).GetProperty("Token").GetString()!;
await controller.ApproveOriginalTitles(new() { Token = token, ItemIds = new() { reapprove.Id.ToString("N") } }, applier, default);
Check(reapprove.Name == "Unknown", "An original title named Unknown must not be discarded as a placeholder");
unchanged = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
unchanged.Libraries[0].Rules.Add(new() { ItemType = "Series", Field = "OriginalTitle" });
plugin.UpdateConfiguration(unchanged);
config = plugin.Configuration;
Check(config.OriginalTitleApprovals.Count == 1, "Adding another item type's rule must preserve the approved movie sample");
unchanged = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
unchanged.Libraries[0].Rules.RemoveAll(r => r.Field == "OriginalTitle" && r.ItemType == "Series");
plugin.UpdateConfiguration(unchanged);
config = plugin.Configuration;
Check(config.OriginalTitleApprovals.Count == 1, "Removing another item type's rule must preserve the approved movie sample");
unchanged = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(config))!;
unchanged.Libraries[0].Rules.RemoveAll(r => r.Field == "OriginalTitle");
plugin.UpdateConfiguration(unchanged);
config = plugin.Configuration;
Check(config.OriginalTitleApprovals.Count == 0, "Removing the approving title rule must revoke its sample approvals");

// Regression for the existing count-based lock comparison: one removal plus one addition.
config.Libraries[0].Rules = new()
{
    new() { ItemType = "Movie", Field = "Name", Language = "en", Lock = false },
    new() { ItemType = "Movie", Field = "Overview", Language = "en", Lock = true },
};
var lockSwap = new TestMovie { Id = Guid.NewGuid(), Name = "Localized title", Overview = "Provider description", LockedFields = new[] { MetadataField.Name } };
lockSwap.SetProviderId(MetadataProvider.Tmdb, "999");
Check(await applier.ApplyAsync(lockSwap, default)
    && lockSwap.LockedFields.SequenceEqual(new[] { MetadataField.Overview }),
    "Equal-sized lock swaps must actually be persisted");
var backup = new OriginalTitleBackup { OriginalName = "First", LastAppliedName = "Third", PendingPreviousName = "Second", PendingWrite = true };
Check(OriginalTitlePolicy.PreservationReason("Second", true, false, null, backup) is null, "Second interrupted title update must recover from its immediately previous title");
Check(OriginalTitlePolicy.PreservationReason("Custom", true, false, null, backup) is not null, "Interrupted writes must protect unrelated edits");
Check(!OriginalTitlePolicy.IsValidTitle("\nBad title"), "Malformed titles must be rejected");
Check(OriginalTitlePolicy.IsValidTitle("Unknown") && OriginalTitlePolicy.IsValidTitle("Null")
    && OriginalTitlePolicy.IsValidTitle("N/A"), "Valid title words must not be blacklisted");
Check(!OriginalTitlePolicy.IsValidTitle("   "), "Whitespace-only title data must be rejected");
var timeoutHandler = new TimeoutOnceHandler();
var timeoutClient = new TmdbLocalizedClient(new ClientFactory(timeoutHandler), NullLogger<TmdbLocalizedClient>.Instance);
Check(await timeoutClient.GetMovieOriginalTitleAsync("1", default) is null, "TMDb original-title timeout must skip the item rather than cancel the task");
Check(await timeoutClient.GetMovieOriginalTitleAsync("1", default) == "Recovered original", "TMDb timeout must not cache failure and must be retryable");
timeoutHandler.FailNext = true;
Check(await timeoutClient.GetMovieAsync("2", "en", default) is null, "Localized-field timeout must also skip the item");
Check((await timeoutClient.GetMovieAsync("2", "en", default))?.Name == "Recovered localized", "Localized-field timeout must be retryable");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
var cancellationPropagated = false;
try { await timeoutClient.GetMovieOriginalTitleAsync("3", cancelled.Token); }
catch (OperationCanceledException) { cancellationPropagated = true; }
Check(cancellationPropagated, "Actual user cancellation must still propagate");
Check(OriginalTitlePolicy.EffectiveRules(new[] { new FieldLanguageRule { ItemType = "Episode", Field = "OriginalTitle" } }, "Episode").Count == 0, "Episode original-title rules must be rejected server-side");
// Exercise the scheduled task itself to verify later items still run after a timeout.
config.Libraries[0].Rules = new() { new() { ItemType = "Movie", Field = "OriginalTitle" } };
config.OriginalTitleApprovedScopes.Add(folder.Id.ToString("N") + ":Movie");
items.Clear();
var timedOutItem = new TestMovie { Id = Guid.NewGuid(), Name = "Timed out item" };
timedOutItem.SetProviderId(MetadataProvider.Tmdb, "900");
var afterTimeout = new TestMovie { Id = Guid.NewGuid(), Name = "Translated title", OriginalTitle = "Original after timeout" };
items.Add(timedOutItem);
items.Add(afterTimeout);
timeoutHandler.FailNext = true;
var timeoutApplier = new FieldLangApplier(library, timeoutClient, NullLogger<FieldLangApplier>.Instance);
var sweep = new ApplyFieldLanguagesTask(library, timeoutApplier, timeoutClient, NullLogger<ApplyFieldLanguagesTask>.Instance);
var progress = new RecordedProgress();
await sweep.ExecuteAsync(progress, default);
Check(timedOutItem.Writes == 0 && afterTimeout.Writes == 1 && progress.Last == 100,
    "A TMDb timeout must not stop the scheduled task from applying later items");
Check(typeof(Plugin).Assembly.GetName().Version == new Version(2, 1, 0, 0), "New release assembly must report version 2.1.0.0");

// Remote lookups may overlap a dashboard metadata lock. Stage responses before any item edits.
config.Libraries[0].Rules = new()
{
    new() { ItemType = "Movie", Field = "Name", Language = "en" },
    new() { ItemType = "Movie", Field = "Overview", Language = "pl" },
};
var lockDuringLookup = new TestMovie { Id = Guid.NewGuid(), Name = "Before lookup", Overview = "Before description" };
lockDuringLookup.SetProviderId(MetadataProvider.Tmdb, "990");
var raceHandler = new CallbackTmdbHandler(requestNumber =>
{
    if (requestNumber == 2) lockDuringLookup.IsLocked = true;
});
var raceClient = new TmdbLocalizedClient(new ClientFactory(raceHandler), NullLogger<TmdbLocalizedClient>.Instance);
var raceApplier = new FieldLangApplier(library, raceClient, NullLogger<FieldLangApplier>.Instance);
Check(!await raceApplier.ApplyAsync(lockDuringLookup, default) && raceHandler.Requests == 2,
    "A full lock arriving during a later lookup must stop the entire application");
Check(lockDuringLookup.Name == "Before lookup" && lockDuringLookup.Overview == "Before description"
    && lockDuringLookup.Writes == 0 && lockDuringLookup.LockedFields.Length == 0,
    "Earlier fetched rules must not leave cached metadata edits when a later lookup observes a full lock");

config.Libraries[0].Rules = new()
{
    new() { ItemType = "Movie", Field = "OriginalTitle" },
    new() { ItemType = "Movie", Field = "Overview", Language = "en" },
};
var originalDuringLock = new TestMovie { Id = Guid.NewGuid(), Name = "Before original lookup", Overview = "Original description" };
originalDuringLock.SetProviderId(MetadataProvider.Tmdb, "991");
var originalRaceHandler = new CallbackTmdbHandler(requestNumber =>
{
    if (requestNumber == 2) originalDuringLock.IsLocked = true;
});
var originalRaceClient = new TmdbLocalizedClient(new ClientFactory(originalRaceHandler), NullLogger<TmdbLocalizedClient>.Instance);
var originalRaceApplier = new FieldLangApplier(library, originalRaceClient, NullLogger<FieldLangApplier>.Instance);
var backupsBeforeRace = config.OriginalTitleBackups.Count;
Check(!await originalRaceApplier.ApplyAsync(originalDuringLock, default)
    && originalRaceHandler.Requests == 2 && originalDuringLock.Writes == 0,
    "A full lock arriving during the original-title lookup must also block previously fetched description rules");
Check(originalDuringLock.Name == "Before original lookup" && originalDuringLock.Overview == "Original description"
    && config.OriginalTitleBackups.Count == backupsBeforeRace,
    "A blocked lookup must not create partial titles, descriptions, or backups");

config.Libraries[0].Rules = new() { new() { ItemType = "Movie", Field = "Name", Language = "en" } };
var newLockDuringLookup = new TestMovie { Id = Guid.NewGuid(), Name = "Before new lock" };
newLockDuringLookup.SetProviderId(MetadataProvider.Tmdb, "992");
var newLockHandler = new CallbackTmdbHandler(_ => newLockDuringLookup.LockedFields = new[] { MetadataField.Genres });
var newLockClient = new TmdbLocalizedClient(new ClientFactory(newLockHandler), NullLogger<TmdbLocalizedClient>.Instance);
var newLockApplier = new FieldLangApplier(library, newLockClient, NullLogger<FieldLangApplier>.Instance);
Check(await newLockApplier.ApplyAsync(newLockDuringLookup, default)
    && newLockDuringLookup.LockedFields.Contains(MetadataField.Name)
    && newLockDuringLookup.LockedFields.Contains(MetadataField.Genres),
    "Lock arrays must preserve unrelated locks added while a request is in flight");

// Re-enable the rule while an interrupted rollback is pending, then resume rollback after restart.
items.Clear();
config.Libraries[0].Rules = new() { new() { ItemType = "Movie", Field = "OriginalTitle" } };
config.OriginalTitleApprovedScopes = new() { folder.Id.ToString("N") + ":Movie" };
var resumeRollback = new TestMovie { Id = Guid.NewGuid(), Name = "Before interrupted rollback", OriginalTitle = "Managed original" };
items.Add(resumeRollback);
await applier.ApplyOriginalTitleOnlyAsync(resumeRollback, default);
resumeRollback.FailNextWrite = true;
try { await controller.RollbackOriginalTitles(default); } catch (IOException) { }
var pendingRecovery = config.OriginalTitleBackups.Single(b => b.ItemId == resumeRollback.Id.ToString("N"));
Check(pendingRecovery.PendingRollback && !pendingRecovery.ManualOverrideDetected, "Interrupted rollback must start as recoverable, not a manual edit");
config.Libraries[0].Rules.Add(new() { ItemType = "Movie", Field = "OriginalTitle" });
dryRun = (OkObjectResult)(await controller.DryRunOriginalTitles(applier, default)).Result!;
token = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Value).GetProperty("Token").GetString()!;
Check((await applier.PreviewOriginalTitleAsync(resumeRollback, default))!.Reason.Contains("rollback", StringComparison.OrdinalIgnoreCase),
    "Preview must explain that a pending rollback temporarily blocks title application");
await controller.ApproveOriginalTitles(new() { Token = token, ApproveLibraries = true }, applier, default);
Check(!await applier.ApplyOriginalTitleOnlyAsync(resumeRollback, default)
    && pendingRecovery.PendingRollback && !pendingRecovery.ManualOverrideDetected,
    "Reapproval must not convert pending rollback into a permanent manual override");
plugin = new Plugin(paths, serializer);
config = plugin.Configuration;
Check(config.OriginalTitleBackups.Single(b => b.ItemId == resumeRollback.Id.ToString("N")) is
    { PendingRollback: true, ManualOverrideDetected: false },
    "Recovery state must remain correct in the on-disk XML after reapproval");
await controller.RollbackOriginalTitles(default);
Check(resumeRollback.Name == "Before interrupted rollback"
    && !resumeRollback.LockedFields.Contains(MetadataField.Name)
    && !config.OriginalTitleBackups.Any(b => b.ItemId == resumeRollback.Id.ToString("N")),
    "Rollback must finish after reapproval and restart without losing its recovery record");
Console.WriteLine($"Passed {passed} safety checks.");

public class Stub<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    public static T Create(Func<MethodInfo, object?[]?, object?> handler)
    {
        var instance = DispatchProxy.Create<T, Stub<T>>();
        ((Stub<T>)(object)instance).Handler = handler;
        return instance;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}

public class TestMovie : Movie
{
    public int Writes { get; private set; }
    public bool FailNextWrite { get; set; }
    public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
    {
        if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated repository failure"); }
        Writes++;
        return Task.CompletedTask;
    }
}

public class TestSeries : MediaBrowser.Controller.Entities.TV.Series
{
    public int Writes { get; private set; }
    public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
    {
        Writes++;
        return Task.CompletedTask;
    }
}

public class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}

public class TmdbHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/404") ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            Content = new StringContent("{\"original_title\":\"日本語の原題\",\"original_name\":\"Série originale\",\"title\":\"Localized title\",\"overview\":\"Provider description\"}", Encoding.UTF8, "application/json")
        });
}

public sealed class TimeoutOnceHandler : HttpMessageHandler
{
    public bool FailNext { get; set; } = true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNext)
        {
            FailNext = false;
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout", new TimeoutException()));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"original_title\":\"Recovered original\",\"title\":\"Recovered localized\"}")
        });
    }
}

public sealed class TestWorkspace : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("fieldlang-safety-");
    public string Path => _directory.FullName;
    public void Dispose() => _directory.Delete(recursive: true);
}

public sealed class RecordedProgress : IProgress<double>
{
    public double Last { get; private set; }
    public void Report(double value) => Last = value;
}

public sealed class CallbackTmdbHandler(Action<int> callback) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        callback(++Requests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"title\":\"Localized during lookup\",\"overview\":\"Fetched description\",\"original_title\":\"Fetched original\"}")
        });
    }
}
