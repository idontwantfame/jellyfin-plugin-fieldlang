# jellyfin-plugin-fieldlang — "Field Language"

Custom Jellyfin plugin: **per-field metadata language**. Titles in one language, descriptions in
another, configured per library and per item type. Written 2026-08-04 for this homelab, intended to
be published to GitHub as a generic plugin (nothing in it is homelab-specific).

**This is real source code, so it has its own git repo** (unlike the compose-only stacks).

- Assembly / namespace: `Jellyfin.Plugin.FieldLang`
- Plugin GUID: `6b2f4a3c-9d51-4f27-8a6e-1c0d7b5e93a4`
- Built against **Jellyfin.Controller 10.11.11** (exact match for the running server), `net9.0`
- Installed at `/home/raxi/jellyfin/config/plugins/Field Language_1.0.0.0/`

## Why it exists

Jellyfin's `PreferredMetadataLanguage` is per-library and all-or-nothing. On 2026-08-04 the whole
library was converted `tr` → `en`; the wanted end state was English titles with Turkish
descriptions, which no Jellyfin setting expresses. Four upstream feature requests, no implementation
— see README for links.

## Architecture — the one decision that matters

**It is NOT an `IRemoteMetadataProvider`.** That is the obvious design and it is a trap. In
`MetadataService.ExecuteRemoteProviders` the merge is:

```csharp
if (replaceData || target.Genres.Length == 0) { target.Genres = source.Genres; }
```

So a provider ordered after TMDb overwrites a *populated* field only when `replaceData == true`,
i.e. only on a **manual full refresh**. On scheduled/automatic refreshes it silently does nothing.
You would test by hand, see it work, and never notice it had stopped working overnight.

Instead: everything runs from an **`IScheduledTask`** (`FieldLangApplyTask`, category
"Field Language"), which sweeps after the normal providers are done. Provider ordering becomes
irrelevant.

An `ItemAdded`/`ItemUpdated` hosted-service hook was built first and **deliberately removed on
2026-08-04** at the user's request — the scheduled task alone was judged enough, and dropping the
hook removes a whole class of re-entrancy and threading failure. If it is ever reinstated, the
loop-breaker was idempotency (saving re-raises the event; pass 2 finds nothing to change and stops),
not a suppression set.

## Load-bearing details

- **The task carries a daily default trigger.** It is the only mechanism, so `GetDefaultTriggers()`
  must return something — with an empty list a fresh install silently does nothing until someone
  presses play by hand. Consequence: a newly added item keeps the core provider's language until
  the next run.
- **Repeat sweeps are free.** `FieldLangApplier.ApplyAsync` writes only on a real difference, so a
  second run over a correct library issues zero DB writes.
- **Never blank a field.** TMDb returns the key present-but-empty for a language it lacks.
  `LocalizedFields` properties are nullable and null means "don't touch". Removing that guard would
  wipe descriptions for every item TMDb has no localized data for.
- **429 is not cached; 404 is.** A rate-limit is transient, so caching it would turn a temporary
  throttle into a permanent "no data" for the process lifetime. A genuine 404 is cached so a
  library sweep doesn't ask hundreds of times.
- **Rules match libraries by id, not name** (`GetCollectionFolders(item)` → virtual-folder id), so
  renaming a library in Jellyfin doesn't orphan its rules.
- **Needs its own TMDb API key.** The core TMDb/OMDb keys are compiled into the server assembly and
  unreachable from a plugin — the same finding already recorded in `jellyfin/CLAUDE.md` about
  MDBList's optional keys.
- **`autoUpdate: false` in `meta.json`** — deliberate. The Trakt plugin's `autoUpdate: true`
  previously overwrote a locally patched DLL here.

## Config model

`PluginConfiguration` → `Libraries[]` (`LibraryRuleSet`) → `Rules[]` (`FieldLanguageRule`:
ItemType + Field + Language). Empty language = leave the field alone. Global settings:
`TmdbApiKey`, `LockAppliedFields` (default true).

**The config page is generic** — it renders from `GET /FieldLang/Schema` (libraries × field
catalog), so adding a field means editing `FieldCatalog` + the TMDb mapper only. No HTML edit.

## Build / install

```bash
cd /home/raxi/jellyfin-plugin-fieldlang
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build Jellyfin.Plugin.FieldLang/Jellyfin.Plugin.FieldLang.csproj -c Release
cp Jellyfin.Plugin.FieldLang/bin/Release/net9.0/Jellyfin.Plugin.FieldLang.dll \
   "/home/raxi/jellyfin/config/plugins/Field Language_1.0.0.0/"
docker restart jellyfin
```

The config page is an **embedded resource** — editing `configPage.html` requires a rebuild, not just
a file copy.

⚠️ **Never put `data-controller` on the page div unless you actually ship that JS file.** It makes
Jellyfin's view manager load a separate module and drive the lifecycle from it; pointing it at a
non-existent path means the controller 404s and the inline `<script>` never receives `pageshow` —
the page renders its static HTML but the Rules section stays empty and no config loads. Cost an
hour on 2026-08-04. Debug trick that found it: extract the inline script into a standalone harness
with stubbed `ApiClient`/`Dashboard` plus the real `/FieldLang/Schema` JSON, run it under
`google-chrome --headless --dump-dom --virtual-time-budget=6000`, and print child counts + captured
`window.onerror` into the DOM. That proved the JS was correct and moved the search to the loader.

Verify after restart:

```bash
K=de1f3574a2d24811a843dcd0bf1e935f
curl -s "http://127.0.0.1:8096/Plugins?api_key=$K" | grep -o '"Name":"Field Language"[^}]*'
curl -s "http://127.0.0.1:8096/FieldLang/Schema?api_key=$K"
docker logs jellyfin --since 2m 2>&1 | grep -i fieldlang
```
