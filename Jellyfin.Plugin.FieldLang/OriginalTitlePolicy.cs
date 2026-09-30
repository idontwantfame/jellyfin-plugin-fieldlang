using Jellyfin.Plugin.FieldLang.Configuration;

namespace Jellyfin.Plugin.FieldLang;

/// <summary>Conservative ownership decisions shared by preview, application, and rollback.</summary>
public static class OriginalTitlePolicy
{
    /// <summary>Checks a candidate for blank or malformed title data without excluding real titles.</summary>
    public static bool IsValidTitle(string? title) => !string.IsNullOrWhiteSpace(title)
        && title.Length <= 1000
        && !title.Any(char.IsControl);

    /// <summary>Explains why an item must be preserved, or returns null when it can be managed.</summary>
    public static string? PreservationReason(
        string name, bool nameLocked, bool itemLocked, string? tmdbId, OriginalTitleBackup? backup)
    {
        if (itemLocked)
        {
            return "item metadata is fully locked";
        }

        if (backup is null)
        {
            return nameLocked ? "existing manual metadata/title lock" : null;
        }

        if (backup.PendingRollback)
        {
            return "rollback is pending; title application is suspended until it finishes";
        }

        if (backup.ManualOverrideDetected)
        {
            return "a later edit was detected; title management is suspended";
        }

        if (backup.TmdbId is not null && !string.Equals(backup.TmdbId, tmdbId, StringComparison.Ordinal))
        {
            return "provider identity changed; review the item again";
        }

        if (!string.Equals(name, backup.LastAppliedName, StringComparison.Ordinal)
            && !(backup.PendingWrite && string.Equals(name,
                backup.PendingPreviousName ?? backup.OriginalName, StringComparison.Ordinal)))
        {
            return "title changed since Field Language's last write; preserving it";
        }

        // Refresh and manual edits cannot be distinguished from a title alone. Keep original
        // titles locked and suspend when that protection has been removed instead of guessing.
        return backup.LockOwnershipRecorded && !backup.PendingWrite && !nameLocked
            ? "title lock was removed; preserving subsequent metadata edits"
            : null;
    }

    /// <summary>Whether rollback may restore a title without replacing a later edit.</summary>
    public static bool CanRollback(string name, OriginalTitleBackup backup, bool itemLocked = false) =>
        !itemLocked && !backup.ManualOverrideDetected
        && (string.Equals(name, backup.LastAppliedName, StringComparison.Ordinal)
            || (backup.PendingRollback && string.Equals(name, backup.OriginalName, StringComparison.Ordinal))
            || (backup.PendingWrite && string.Equals(name,
                backup.PendingPreviousName ?? backup.OriginalName, StringComparison.Ordinal)));

    /// <summary>Filters unsupported rules and makes original title take precedence over localized title.</summary>
    public static List<FieldLanguageRule> EffectiveRules(IEnumerable<FieldLanguageRule> rules, string itemType)
    {
        var result = rules.Where(r => r.ItemType == itemType
            && FieldCatalog.Find(itemType, r.Field) is not null
            && (r.Field == "OriginalTitle" || !string.IsNullOrWhiteSpace(r.Language))).ToList();
        if (result.Any(r => r.Field == "OriginalTitle"))
        {
            result.RemoveAll(r => r.Field == "Name");
        }

        return result;
    }
}
