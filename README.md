# Field Language

A Jellyfin plugin that pulls **individual metadata fields in different languages**.

Jellyfin's metadata language is a single per-library setting: everything comes back in one language,
or nothing does. This plugin lets you say *titles in English, descriptions in Turkish, genres in
German* — configured per library and per item type.

This has been requested upstream for years without an implementation:

- [jellyfin#10076 — Separate Language Settings for Titles and Posters](https://github.com/jellyfin/jellyfin/issues/10076)
- [jellyfin-web#332 — Metadata language priority](https://github.com/jellyfin/jellyfin-web/issues/332)
- [jellyfin#16400 — Allow metadata language override on manual refresh](https://github.com/jellyfin/jellyfin/issues/16400)
- [features.jellyfin.org#610 — Metadata in multiple languages](https://features.jellyfin.org/posts/610/metadata-in-multiple-languages)

The existing [Polyglot](https://github.com/Maronato/jellyfin-plugin-polyglot) plugin solves an
adjacent but different problem — whole-library mirrors via hardlinks, one library per language.
This one operates at field granularity inside a single library.

## Supported fields

| Item type | Fields |
|---|---|
| Movie | Title, Description, Tagline, Genres |
| Series | Title, Description, Tagline, Genres |
| Season | Title, Description |
| Episode | Title, Description |

The list is deliberately limited to what TMDb actually returns per-language. Season and episode
payloads carry only name and overview, so offering a Tagline control there would render something
that could never do anything.

Adding a field means one entry in `FieldCatalog` plus one line in the TMDb mapper — the config page
renders itself from the catalog via `GET /FieldLang/Schema`, so the UI needs no edit.

## Requirements

- Jellyfin **10.11.x**
- A **TMDb API key** (free, v3). The server's built-in key is compiled into the core assembly and is
  not reachable from a plugin, so this one needs its own.

## How it works, and why it isn't a metadata provider

The obvious design — register an `IRemoteMetadataProvider` that runs after TMDb and overwrites
specific fields — **silently fails on half of all refreshes**. In `MetadataService.ExecuteRemoteProviders`:

```csharp
MergeData(result, temp, [], replaceData, false);
// and inside MergeBaseItemData:
if (replaceData || target.Genres.Length == 0) { target.Genres = source.Genres; }
```

A later provider overwrites a populated field only when `replaceData` is true — that is, only on a
manual full refresh. On the scheduled refreshes that do most of the work, `replaceData` is false and
the override does nothing at all. You would test it by hand, see it work, and never notice it had
stopped.

So instead everything runs from a scheduled task, **Apply per-field metadata languages**, which
sweeps the configured libraries and rewrites the mapped fields after the normal providers are done.
Provider ordering stops mattering entirely.

The task is registered with a **daily** default trigger. Run it by hand from Dashboard → Scheduled
Tasks to apply a config change immediately, or add triggers there to run it more often. The tradeoff
of not hooking library events is that a newly added item keeps the core provider's language until
the next run.

### Two details worth knowing

**Repeat runs are free.** The applier writes only when a value actually differs, so a second sweep
over an already-correct library issues no database writes at all. The task is safe to schedule
aggressively and safe to re-run by hand.

**Empty is never written.** TMDb answers a request for a language it lacks with the field present
but empty. Blanking a populated description is worse than leaving the wrong language in place, so
a null/empty localized value means "keep what the core provider wrote".

## Locking

With **Lock applied fields** on (the default), each applied field is added to the item's
`LockedFields`, so the normal provider stops overwriting it. With it off, the provider rewrites the
field on every refresh and this plugin puts it back afterwards — still correct, but the wrong
language is briefly visible.

Tagline has no corresponding `MetadataField` in Jellyfin and therefore cannot be locked; it always
relies on re-application. The config page marks it.

## Building

No local .NET SDK required:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build Jellyfin.Plugin.FieldLang/Jellyfin.Plugin.FieldLang.csproj -c Release
```

Then copy `bin/Release/net9.0/Jellyfin.Plugin.FieldLang.dll` alongside a `meta.json` into
`<jellyfin-config>/plugins/Field Language_1.0.0.0/` and restart the server.

## License

MIT
