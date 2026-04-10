# 002: Three Alert Types Only

## Context

GlassWire and similar tools generate alerts for many event types: blocked connections, rule changes, new processes, host changes, WiFi changes, and more. High alert volume leads to alert fatigue — users stop reading them and the feature becomes decorative.

## Decision

Beholder NMT has exactly three alert types:

1. **NewProcess** — a binary path accesses the network for the first time. Fires at most once per unique path over the lifetime of the installation.
2. **HashChanged** — a tracked binary's SHA-256 changes. Fires once per update per binary. Verified-signature updates are demoted to informational.
3. **ChainError** — hash chain integrity verification detects a mismatch. Should never fire under correct operation.

Firewall block events and rule change events are NOT alerts. They are visible in the Firewall tab's "Recent Firewall Activity" strip and are recorded in the chain-hashed event log for audit purposes, but they do not appear in the Alerts view.

## Consequences

- Alert volume on a typical machine is near zero on most days — the Alerts tab is meaningful, not noisy
- No pruning, aggregation, or retention policies needed — the total alert count over years is in the low thousands
- No dismiss/delete UX needed — alerts use a read/unread model (dimmed once viewed, never removed)
- The chain-hashed event log still records everything for audit and forensic purposes — nothing is lost
- Blocked-app-retries are truly silent — the set-and-forget firewall model works as designed
- Future alert types (e.g., `Remote` for aggregator-pushed rule changes) can be added to the enum without changing the infrastructure
