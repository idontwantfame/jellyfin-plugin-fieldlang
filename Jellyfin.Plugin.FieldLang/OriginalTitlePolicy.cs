using Jellyfin.Plugin.FieldLang.Configuration;

namespace Jellyfin.Plugin.FieldLang;

/// <summary>The requested display-title action for a known source language.</summary>
public enum DisplayTitleAction
{
    /// <summary>Preserve the current title.</summary>
    Keep,
    /// <summary>Use the film or series' original title.</summary>
    Original,
    /// <summary>Use an available translation in the chosen target language.</summary>
    Localized,
}

/// <summary>Conservative ownership decisions shared by preview, application, and rollback.</summary>
public static class OriginalTitlePolicy
{
    /// <summary>Normalizes ISO source-language codes for comparison.</summary>
    public static string NormalizeLanguage(string? language) => (language ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Evaluates source-language conditions. Conflicting duplicates keep the current title.</summary>
    public static bool ShouldUpdateDisplayTitle(FieldLanguageRule rule, string? language) =>
        GetDisplayTitleAction(rule, language) == DisplayTitleAction.Original;

    /// <summary>Resolves a language action. Unknown languages and conflicting decisions are preserved.</summary>
    public static DisplayTitleAction GetDisplayTitleAction(FieldLanguageRule rule, string? language) =>
        GetDisplayTitleDecision(rule, language).Action;

    /// <summary>Resolves a source-language decision and optional translation target.</summary>
    public static (DisplayTitleAction Action, string Language) GetDisplayTitleDecision(FieldLanguageRule rule, string? language)
    {
        if (!rule.UseLanguageRules) return (DisplayTitleAction.Original, string.Empty);
        var code = NormalizeLanguage(language);
        // TMDb uses und/xx for undetermined/no language. Never guess from audio tracks or filenames.
        if (code.Length != 2 || !code.All(char.IsAsciiLetter) || code is "und" or "xx") return (DisplayTitleAction.Keep, string.Empty);
        var matches = rule.LanguageRules.Where(r => NormalizeLanguage(r.Language) == code).ToList();
        var actions = matches.Select(r => Decision(r.UpdateDisplayTitle, r.TitleLanguage)).Distinct().ToList();
        return actions.Count == 0 ? Decision(rule.DefaultUpdateDisplayTitle, rule.DefaultTitleLanguage)
            : actions.Count == 1 ? actions[0] : (DisplayTitleAction.Keep, string.Empty);
    }

    private static (DisplayTitleAction Action, string Language) Decision(bool original, string? target)
    {
        var language = NormalizeLanguage(target);
        if (language.Length == 0) return (original ? DisplayTitleAction.Original : DisplayTitleAction.Keep, string.Empty);
        var parts = language.Split('-');
        if (original || parts.Length > 2 || parts.Any(p => p.Length != 2 || !p.All(char.IsAsciiLetter)))
            return (DisplayTitleAction.Keep, string.Empty);
        return (DisplayTitleAction.Localized, language);
    }

    /// <summary>Describes conditions in a stable order for approval scope identity.</summary>
    public static string LanguageRuleIdentity(FieldLanguageRule rule)
    {
        if (!rule.UseLanguageRules) return string.Empty;
        // Preserve 2.2 approval identities when translated-title actions are not in use.
        if (string.IsNullOrWhiteSpace(rule.DefaultTitleLanguage) && !rule.LanguageRules.Any(r => !string.IsNullOrWhiteSpace(r.TitleLanguage)))
            return System.Text.Json.JsonSerializer.Serialize(new
        {
            rule.DefaultUpdateDisplayTitle,
            Rules = rule.LanguageRules.GroupBy(r => NormalizeLanguage(r.Language), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { Language = g.Key, Update = g.All(r => r.UpdateDisplayTitle) }),
        });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            DefaultAction = Decision(rule.DefaultUpdateDisplayTitle, rule.DefaultTitleLanguage).Action.ToString(),
            DefaultTarget = Decision(rule.DefaultUpdateDisplayTitle, rule.DefaultTitleLanguage).Language,
            Rules = rule.LanguageRules.Select(r => NormalizeLanguage(r.Language)).Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .Select(c => new { Language = c, Action = GetDisplayTitleDecision(rule, c).Action.ToString(), Target = GetDisplayTitleDecision(rule, c).Language }),
        });
    }

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
    public static bool CanRollback(string name, OriginalTitleBackup backup, bool itemLocked = false, bool nameLocked = true) =>
        !itemLocked && !backup.ManualOverrideDetected
        // A removed title lock relinquishes ownership just as it does during application.
        // Pending operations may legitimately have no lock because their write did not finish.
        && (!backup.LockOwnershipRecorded || nameLocked || backup.PendingWrite || backup.PendingRollback)
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
