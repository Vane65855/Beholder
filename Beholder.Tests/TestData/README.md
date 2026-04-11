# Test Data

This directory holds test fixtures used by `Beholder.Tests`.

## `beholder-test.mmdb`

A tiny (~1.3 KB) MaxMind DB file used by `DbIpProviderTests` to exercise
`DbIpProvider` without depending on the full 8 MB DB-IP Lite production
database.

### Schema

The fixture matches the subset of the GeoLite2-Country / DB-IP-Country
schema that `DbIpProvider` actually reads:

```
{"country": {"iso_code": "XX"}}
```

### Contents

| Network        | Country |
|----------------|---------|
| `8.8.8.8/32`   | `US`    |
| `1.1.1.1/32`   | `AU`    |
| `78.46.0.0/16` | `DE`    |

`203.0.113.1` (RFC 5737 TEST-NET-3) is intentionally absent so that
`DbIpProviderTests.Resolve_UnknownIp_ReturnsUnknown` exercises the
"not in database" branch.

### Regeneration

The fixture is committed as a binary blob; regeneration is only needed
when the schema or test coverage changes.

```
pip install mmdb_writer
python generate-fixture.py
```

The generator script lives next to this file and uses
[`mmdb_writer`](https://pypi.org/project/mmdb_writer/) — a pure-Python
writer for the MaxMind DB format. There is no .NET NuGet package that
writes MMDB files, which is why this one-shot offline process is used.
