# Beholder.Tools.GeoIpFetcher

Console tool that downloads the DB-IP Lite IP-to-country MMDB from
[db-ip.com](https://db-ip.com/db/lite.php) and places it in a target
directory (default: `<repo-root>/data/`) so the daemon's csproj can
pick it up.

## Usage

From the repo root:

```
dotnet run --project Beholder.Tools.GeoIpFetcher
```

Or with an explicit output directory:

```
dotnet run --project Beholder.Tools.GeoIpFetcher -- --output some/other/path
```

On success, the tool writes two files:

- `<output>/dbip-country-lite.mmdb` — the database (~8 MB, monthly edition)
- `<output>/ATTRIBUTION.md` — CC BY 4.0 attribution notice

Exit code `0` on success, `1` on any failure (network, 404, extraction).

## When to run

- **Once per dev setup**, after cloning. The daemon gracefully degrades
  to `NullGeoIpResolver` if the file is missing, but real country data
  is only available once the fetcher has populated `data/`.
- **Monthly**, when DB-IP publishes the next edition. The tool always
  fetches whatever month is current on db-ip.com (falling back to the
  previous month if the current one isn't published yet).

## How it integrates with the build

`Beholder.Daemon/Beholder.Daemon.csproj` has a conditional `<Content>`
entry that copies `data/dbip-country-lite.mmdb` into
`bin/.../data/` **if the file exists**. If it doesn't, the daemon
runs with `NullGeoIpResolver` and logs a one-line warning on startup.
The build never requires network access.

## License

Data: DB-IP Lite under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
The fetcher writes `ATTRIBUTION.md` alongside the data to carry the
attribution obligation forward on any redistribution.

Code (this tool): same AGPL-3.0 as the rest of the repo.
