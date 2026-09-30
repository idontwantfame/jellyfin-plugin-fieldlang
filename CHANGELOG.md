# Changelog

## 2.1.0.0 — Unreleased

- Add per-item original titles for movies and series on Jellyfin 12, using populated valid Jellyfin
  original titles and falling back to TMDb original titles through provider IDs.
- Add read-only previews, selected sample application, and explicit approval of libraries and
  future imports before the scheduled task can apply the rule.
- Preserve existing manual locks and later title edits; original titles always use a title lock.
  Respect full metadata locks even when added after initial application, including during rollback.
- Save title backups before writes, recover interrupted applications and rollbacks, preserve
  manual sort-title edits, and revoke rules and approvals during rollback.
- Persist pending backups again before lock-only retries when the first configuration save failed.
- Keep server-owned backups and approvals across unrelated or stale dashboard configuration saves;
  revoke sample approvals when their original-title rules are removed.
- Skip transient TMDb timeouts per item without aborting the sweep; retain actual user cancellation.
- Keep sweeps running after repository-level timeouts unrelated to task cancellation, retain
  recovery journals for retry, and report completion for empty libraries.
- Finish remote lookups before editing shared metadata, respect full item locks added during
  lookups, and preserve unrelated field locks added while waiting for TMDb.
- Discard responses when provider identity or original-title source metadata changes during a
  lookup; report the reason in dry-run output and retry against corrected metadata on the next run.
- Recheck title edits and metadata locks after dry-run lookups so concurrently protected items
  are not presented as eligible sample changes.
- Keep interrupted rollback recoverable when an original-title rule is reenabled and reapproved;
  pending recovery is distinct from a manual title override.
- Respect removed title locks during rollback, even before the scheduled task detects the edit;
  retain interrupted-write recovery and backups for deliberately skipped items.
- Accept legitimate titles such as “Unknown” without treating their words as placeholders.
- Fix equal-sized metadata-lock swaps and remove obsolete documentation about an event hook.
- Add regression checks for application, approval, rollback, timeout handling, XML-backed plugin
  restarts, and the release assembly version. Jellyfin 12.1/Neptune live validation remains pending.

This release targets Jellyfin 12.0.0.0 ABI and .NET 10. The published repository manifest remains
unchanged until a release archive and its checksum are available.
