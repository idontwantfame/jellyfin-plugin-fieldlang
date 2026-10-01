# Changelog

## 2.1.0.0

- Add GitHub build/test artifacts and tag/manual releases, catalogue publication, archive
  checksums, and release changelogs generated from commits since the previous version tag.
- Point installation documentation to idontwantfame's fork and credit rbcetin as the original author.
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
- Respect cancelled requests even when TMDb metadata is already cached.
- Keep sweeps running after repository-level timeouts unrelated to task cancellation, retain
  recovery journals for retry, and report completion for empty libraries.
- Snapshot scheduled-task rules under the metadata mutation gate to avoid racing rollback or
  configuration saves; propagate cancellation even when no enabled rules remain.
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

<!-- generated-commit-changelog -->

### Commits in this release

- docs: document fork releases and credit the original author ([e071ed3](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/e071ed37bc4ef31994d20634e7a8fdacc4996338))
- ci: build package and publish plugin releases with commit changelogs ([c0dae36](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/c0dae36e08ff1b92ec9e32d712c5b1f28d29f8cb))
- docs: distinguish local original-title installation from published releases ([ec04831](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/ec0483123272c08bab3f6c179aa484489f792d94))
- fix: respect cancellation before serving cached TMDb metadata ([e5242a4](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/e5242a4d20bf0c6ac7e458f68abe9e6976e0d8fa))
- fix: serialize scheduled-task rule snapshots with configuration changes ([a6b53b6](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/a6b53b6025565928d1085f1369ca6f63fb8ea3c5))
- fix: persist recovery journals before lock-only retries ([63ee1a0](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/63ee1a034a5f195c4083f8a39d5cfcb0d7e0b7df))
- fix: revalidate title preservation after dry-run lookups ([7e68e69](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/7e68e69259ca4b52ce615da2bb538d17cc3fe4ef))
- fix: discard TMDb responses after metadata identity changes ([9ab6576](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/9ab65769530ce48ff468f9f63708a73d1600a69f))
- fix: keep metadata sweeps resilient to repository timeouts ([5670b27](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/5670b27be679fbe2fca0e46257cc72b0e7b49a36))
- fix: respect removed title locks during rollback ([ddd66b3](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/ddd66b3709ffae05a9618b359b689d818785e2e8))
- docs: document original-title rollout validation and rollback ([bc7d660](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/bc7d6607ece9d4726d67a3f3b22645e9bca18e0e))
- test: cover original-title approvals recovery and metadata safety ([07662e4](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/07662e450f63142aecb26a3d050681393a1d3fae))
- chore: prepare version 2.1.0 for Jellyfin 12 ([d0785ef](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/d0785efefd795e654fce646df66110505ef17001))
- feat: add reviewed server-stored original titles with recoverable rollback ([a63245f](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/a63245fa84f6b078f044049e50387a458787d71f))
- fix: persist metadata lock changes when the lock count stays equal ([428efef](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/428efef0acbe97b2de62afff208743cb80cb5ef9))
- fix: treat transient TMDb timeouts as retryable lookup failures ([622348c](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/622348c6b46c1136ed0a410f7ab921eff0637187))
- Target Jellyfin 12 (net10.0, ABI 12.0.0.0) as v2.0.0.0 ([84ed6c5](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/84ed6c5ac20bef8c8db6deb0c22131e3e2992dda))
- Disclose AI assistance in the README ([3b7b4c6](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/3b7b4c669302ba2c9f71f3754794f08724e36ded))
- Add plugin repository manifest for v1.0.0.0 ([f3e08cd](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/f3e08cdae9e0b8be2f7f4bf811be0036b8dc375d))
- Trim README, drop CLAUDE.md from the repo ([3bfbbf9](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/3bfbbf91238430ffa55237fd027a93509e39df43))
- Make locking per rule instead of one global toggle ([f6b100a](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/f6b100a79eec9788215fd2d631b950b0fb395a34))
- Collapse libraries into accordions; define each field in one place ([18b7b3f](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/18b7b3f1873937295d9ebee0e3a547ae9688bd54))
- Drop the ItemAdded/ItemUpdated hook; scheduled task is the only mechanism ([766b908](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/766b908a5369086150b385f275ed75c2988294fe))
- Config page: link TMDb signup and API settings from the key field ([e107fcc](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/e107fcc1125a8ff2cd5649014b67eeac0fc0a21e))
- Field Language: per-field metadata language plugin for Jellyfin ([5b2e7d1](https://github.com/idontwantfame/jellyfin-plugin-fieldlang/commit/5b2e7d19be9aae7513a47536e11d50f33a6afe3e))
