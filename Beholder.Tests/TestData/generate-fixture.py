"""
Regenerates beholder-test.mmdb, a tiny DB-IP Lite-shaped MMDB fixture used by
Beholder.Tests/DbIpProviderTests.

Requirements:
    pip install mmdb_writer

Run from this directory:
    python generate-fixture.py

The resulting beholder-test.mmdb (~1 KB) is committed to the repository and
copied next to the test binary via the Beholder.Tests.csproj <Content> entry.

The fixture schema matches the MaxMind/DB-IP GeoLite2-Country shape at the
single key we actually read: {"country": {"iso_code": "XX"}}.
"""

from netaddr import IPNetwork, IPSet
from mmdb_writer import MMDBWriter


def main() -> None:
    writer = MMDBWriter(
        ip_version=6,
        database_type="DB-IP-Country",
        languages=["en"],
        description={"en": "Beholder test fixture"},
        ipv4_compatible=True,
    )
    writer.insert_network(IPSet([IPNetwork("8.8.8.8/32")]), {"country": {"iso_code": "US"}})
    writer.insert_network(IPSet([IPNetwork("1.1.1.1/32")]), {"country": {"iso_code": "AU"}})
    writer.insert_network(IPSet([IPNetwork("78.46.0.0/16")]), {"country": {"iso_code": "DE"}})
    writer.to_db_file("beholder-test.mmdb")
    print("Wrote beholder-test.mmdb")


if __name__ == "__main__":
    main()
