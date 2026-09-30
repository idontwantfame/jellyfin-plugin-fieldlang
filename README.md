# Field Language

Jellyfin plugin that pulls individual metadata fields in different languages.

Jellyfin's metadata language is one setting per library: everything comes back in one language or
nothing does. This lets you keep titles in English while descriptions come in Turkish, or any other
combination, set per library and per item type.

## Fields

| Item type | Fields |
|---|---|
| Movie, Series | Title, **Original title**, Description, Tagline, Genres |
| Season, Episode | Title, Description |

Limited to what TMDb returns per language. Season and episode payloads only carry name and overview.

If TMDb has no data for a field in the language you asked for, the existing value is kept rather
than blanked.

## Original titles

For movies and series, enable **Original title** instead of entering a language code. This is a
per-item rule: it uses Jellyfin's valid populated `OriginalTitle` first, then TMDb's `original_title`
(movie) or `original_name` (series) through the item's TMDb provider id. It falls back to the
existing display title when neither source has a usable value. Episodes are intentionally excluded:
their original-title metadata is not reliable enough to change a server title safely.

This changes the Jellyfin server's `Name`, so native clients such as Neptune see it; it is not a
web UI overlay. An explicitly configured Jellyfin sort title is preserved. Otherwise Jellyfin's
normal sort title follows the changed display title, keeping title and alphabetical browsing
consistent.

Before enabling the scheduled task across a library, save the rule and use **Dry-run original
titles** on the plugin page. It lists every configured movie/series, its current and proposed
title, and the source/reason without writing metadata. Saving a new rule does **not** approve it:
the scheduled task will skip its original titles until you explicitly approve selected samples
or the reviewed libraries. Preview tokens expire after 30 minutes or after settings change.
Check representative Polish, English, and other-language samples through Jellyfin's API and Neptune
before approving the libraries and future imports. Sample application changes only titles/locks,
even if descriptions or other fields have separate language rules configured.

Each changed title is journaled in the plugin configuration with its prior display and explicit
sort title and the plugin's title-lock ownership. The journal is saved **before** the item write,
and interrupted applications and rollbacks can resume safely. Dashboard configuration saves cannot
replace the server's journal with an older copy.
Reapproving a rule during an interrupted rollback does not discard recovery state; pending rollback
blocks title application until it finishes and is not classified as a manual title edit.
Sample approvals survive unchanged saves, library renames, API key changes, and edits to unrelated
field rules. Removing an approving original-title rule revokes approvals for its samples.

**Rollback original titles** revokes approvals, removes the original-title rules, and restores
titles still owned by Field Language. It removes only title locks recorded as added by the plugin,
leaves all unrelated locks in place, and preserves subsequent manual sort edits. Later manual
display-title edits are skipped and their backups retained in the plugin configuration for manual
recovery. Legacy backups without recorded lock ownership retain their locks conservatively.

Original title takes precedence over a fixed-language Title rule; saving the configuration removes
the conflicting Title rule. Original-title management **always locks Name**. Existing title locks
or fully locked items are skipped on the first run to protect deliberate manual titles. A full item
lock added later blocks both title application and rollback; the retained backup can be restored
after you unlock the item and deliberately retry rollback. Already-correct
original titles are journaled and locked too; subsequent runs perform no item writes. If the title
changes, its provider identity changes, or its lock is removed, management is suspended and the
current title is preserved. A changed title alone cannot reliably distinguish a provider refresh
from a person's edit, so the plugin does not automatically overwrite either after protection is lost.

Suggested rollout:

1. Enable the rule in one library and save it. Run the dry-run; it is read-only.
2. Select a few Polish, English, and other-language rows and use **Apply selected samples**.
3. Confirm the server `Name` using your user's item endpoint, `GET /Users/{userId}/Items/{itemId}`, and open the same item in Neptune. Verify descriptions and artwork remain unchanged.
4. Run a normal metadata refresh on the samples and repeat the task; the locked titles should remain unchanged. Edit one sample title manually and verify the next run preserves your edit.
5. Run another dry-run, then use **Approve reviewed libraries and future imports**. Run the scheduled task. Import one new item and run the task again to verify the same per-item treatment.
6. If the result is not wanted, use rollback. It restores recorded titles without touching media files, paths, watch state, artwork, or descriptions, and prevents the next task from reapplying the rule.

Administrator API tools: `POST /FieldLang/OriginalTitles/DryRun` returns a `Token` and review rows;
`POST /FieldLang/OriginalTitles/Approve` accepts `{ "Token": "...", "ItemIds": ["..."], "ApproveLibraries": false }`
for immediate samples, or `ApproveLibraries: true` to authorize the reviewed scopes and future imports.
`POST /FieldLang/OriginalTitles/Rollback` revokes approvals and restores safe entries. These require
Jellyfin administrator authentication. No external metadata or media sidecar files are written.

## Requirements

- Jellyfin 12.x (plugin 2.x) or Jellyfin 10.11.x (plugin 1.x)
- A TMDb API key (free, v3). Jellyfin's own key is compiled into the server assembly and can't be
  reached from a plugin.

## Install

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/rbcetin/jellyfin-plugin-fieldlang/main/manifest.json
```

Then install Field Language from the catalogue and restart.

Or do it by hand: unzip the [release](https://github.com/rbcetin/jellyfin-plugin-fieldlang/releases)
into `<config>/plugins/Field Language_<version>/` and restart.

Either way, finish in Dashboard → Plugins → Field Language: paste your TMDb key, expand a library,
put a language code next to the fields you want, save.

Rules are applied by the **Apply per-field metadata languages** scheduled task, which runs daily.
Run it from Dashboard → Scheduled Tasks to apply a change immediately.

## Locking

Language rules have a lock toggle, on by default. Original-title rules always lock the title.
Locking pins the field so Jellyfin's normal metadata
provider stops overwriting it. Unlocked, the provider rewrites it on every refresh and the next task
run puts it back, so the wrong language shows in between.

Unticking a lock removes it, so you can always release one from the config page.

Tagline has no lock in Jellyfin and always works the unlocked way.

## How it works

Everything runs from the scheduled task, which rewrites the mapped fields after the normal providers
have finished.

It is deliberately not an `IRemoteMetadataProvider`. That's the obvious design and it doesn't work.
`MetadataService.ExecuteRemoteProviders` merges with:

```csharp
if (replaceData || target.Genres.Length == 0) { target.Genres = source.Genres; }
```

A provider ordered after TMDb only overwrites a populated field when `replaceData` is true, which
means manual full refreshes only. On the scheduled refreshes that do most of the work it does
nothing at all. Running separately avoids provider ordering entirely.

The task only writes when a value actually differs, so re-running it over a correct library costs no
database writes.

## Adding a field

One entry in `FieldCatalog`, which carries the field's id, label, lock field and how to copy the
value onto an item. The config page renders itself from that catalog over `GET /FieldLang/Schema`,
so there's no UI to update.

## Build

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.FieldLang/Jellyfin.Plugin.FieldLang.csproj -c Release
```

Output lands in `Jellyfin.Plugin.FieldLang/bin/Release/net10.0/`.

Run the executable regression suite (no additional test framework required):

```bash
dotnet run --project tests/FieldLang.SafetyTests.csproj
```

It exercises the plugin's actual applier and administrator endpoints with mocked Jellyfin storage
and TMDb responses: approval gating, sample isolation, Polish/English/other-language titles,
movies/series, original-title precedence, idempotency, manual locks/edits, missing data, lock swaps,
interrupted writes/rollbacks, stale settings saves, rollback sorting, and approval revocation.
Configuration saves use real XML files in a temporary directory, and plugin reloads verify that
Unicode titles, pending-write/rollback markers, and approvals survive disk serialization.
Timeout regressions verify that the scheduled task continues to later items while real cancellation
still propagates. Lookups finish before metadata edits, and regressions cover full metadata locks
and unrelated field locks added during those lookups, plus rule reapproval during interrupted rollback.
Temporary test files are removed when the test process finishes normally.
This suite does not replace live Jellyfin 12.1/Neptune validation on the target server.

The working build is **2.1.0.0**, described in [CHANGELOG.md](CHANGELOG.md). The repository manifest
continues to list existing published releases; add the new version only after its release archive
has been published and its checksum calculated.

## Prior art

[Polyglot](https://github.com/Maronato/jellyfin-plugin-polyglot) solves a related problem a
different way: whole mirrored libraries, one per language, via hardlinks. This plugin works at field
level inside a single library.

Long-standing upstream requests for the same thing:
[#10076](https://github.com/jellyfin/jellyfin/issues/10076),
[jellyfin-web#332](https://github.com/jellyfin/jellyfin-web/issues/332),
[#16400](https://github.com/jellyfin/jellyfin/issues/16400).

## AI disclosure

This plugin was written with substantial AI assistance (Claude), as
[Jellyfin's LLM policy](https://jellyfin.org/docs/general/contributing/llm-policies/) asks
third-party projects to state.

It is not untested output. It was verified end to end against a live Jellyfin 10.11.11 library —
699 items swept, titles left in English while descriptions came back in Turkish, and fields with no
data in the target language left alone rather than blanked. The task-versus-provider design came
from reading `MetadataService.ExecuteRemoteProviders` rather than guessing at it.

Bugs are mine. Issues and pull requests welcome.

## License

MIT
