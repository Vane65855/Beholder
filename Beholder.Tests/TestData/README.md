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

### Regeneration (rare)

The fixture is committed as a binary blob; regeneration is only needed
when the schema or the table above changes. There is no maintained
.NET NuGet package that *writes* MMDB files, so this one-shot offline
step uses Python's [`mmdb_writer`](https://pypi.org/project/mmdb_writer/).

Save the following as `generate-fixture.py` in this directory, run it
once, commit the updated `beholder-test.mmdb`, and delete the script:

```python
from netaddr import IPNetwork, IPSet
from mmdb_writer import MMDBWriter

writer = MMDBWriter(
    ip_version=6,
    database_type="DB-IP-Country",
    languages=["en"],
    description={"en": "Beholder test fixture"},
    ipv4_compatible=True,
)
writer.insert_network(IPSet([IPNetwork("8.8.8.8/32")]),   {"country": {"iso_code": "US"}})
writer.insert_network(IPSet([IPNetwork("1.1.1.1/32")]),   {"country": {"iso_code": "AU"}})
writer.insert_network(IPSet([IPNetwork("78.46.0.0/16")]), {"country": {"iso_code": "DE"}})
writer.to_db_file("beholder-test.mmdb")
```

Then:

```
pip install mmdb_writer netaddr
python generate-fixture.py
rm generate-fixture.py
```

The repository intentionally stays 100% C#; the Python snippet lives
here only for the rare regeneration case, not as a checked-in tool.
