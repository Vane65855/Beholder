# Beholder.Tools.OuiFetcher

Console tool that downloads the IEEE Organizationally Unique Identifier (OUI)
registry CSV from [standards-oui.ieee.org](https://standards-oui.ieee.org/oui/oui.csv)
and places it in a target directory (default: `<repo-root>/data/`) so the
daemon's csproj can pick it up.

## Usage

From the repo root:

```
dotnet run --project Beholder.Tools.OuiFetcher
```

Or with an explicit output directory:

```
dotnet run --project Beholder.Tools.OuiFetcher -- --output some/other/path
```

On success, the tool writes / updates two files:

- `<output>/oui.csv` — the OUI registry (~4 MB plain CSV, ~30k MA-L rows)
- `<output>/ATTRIBUTION.md` — gains an IEEE OUI section if not already present

Exit code `0` on success, `1` on any network or write failure (no partial
file is left on disk).

## When to run

- **Once per dev setup**, after cloning. The daemon gracefully degrades to
  null vendor lookups if the file is missing, but real vendor names are
  only available once the fetcher has populated `data/`.
- **Occasionally**, when IEEE has registered new prefixes. IEEE updates
  the list approximately weekly; vendor lookups for newly-registered
  hardware will return null until the snapshot is refreshed.

## How it integrates with the build

`Beholder.Daemon/Beholder.Daemon.csproj` has a conditional `<Content>` entry
that copies `data/oui.csv` into `bin/.../data/` **if the file exists**.
If it doesn't, the daemon's `OuiVendorLookup` logs a one-line warning at
startup and every `GetVendor` call returns null — daemon stays functional,
LAN devices just don't get vendor names. The build never requires network
access.

## License

Data: IEEE OUI assignments are public information; the registry is
distributed without any redistribution restriction. We record the source
in `ATTRIBUTION.md` for transparency.

Code (this tool): same AGPL-3.0 as the rest of the repo.
