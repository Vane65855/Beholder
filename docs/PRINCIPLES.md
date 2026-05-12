# Design Principles

This document defines the engineering principles that govern all design decisions in Beholder NMT. Every class, interface, method, and architectural choice must be justifiable against these principles. If a principle and a shortcut conflict, the principle wins.

## SOLID

### Single Responsibility Principle

Every class has exactly one reason to change. If you can describe what a class does using the word "and", it has too many responsibilities.

```
Good:
  EtwFlowSource       — reads network events from ETW
  GeoIpResolver       — maps IP addresses to country codes
  HashChainWriter     — appends events to the chain-hashed log

Bad:
  NetworkManager      — collects traffic AND manages firewall rules AND resolves GeoIP
```

When adding a new feature, your first question is: "Which existing class is responsible for this?" If the answer is "none" or "this class, but it would be a stretch," create a new class.

### Open/Closed Principle

Types are open for extension, closed for modification. In practice this means:

- New platform support (e.g., macOS) = a new `IFlowSource` implementation in a new project. Zero changes to Core or Daemon.
- New alert type = a new variant in the `AlertKind` enum and a new handler class. The alert pipeline dispatches by kind; existing handlers are untouched.
- New uplink message type = a new `oneof` variant in the `.proto` file and a new handler. Existing message handlers are untouched.

If adding a feature requires modifying 5+ existing files, the abstraction is wrong.

### Liskov Substitution Principle

Any implementation of an interface can be swapped in without changing the behavior of the consuming code. This means:

- `IFlowSource` implementations on Windows and Linux produce the same event model even though the underlying OS mechanisms are completely different
- `IFirewallController` implementations on both platforms accept and enforce the same rule format
- Test fakes/mocks can substitute for real implementations without special configuration

### Interface Segregation Principle

No class should be forced to depend on methods it doesn't use.

```
Good:
  IFlowSource           — StartCapture, StopCapture, OnFlowEvent
  IFirewallController   — AddRule, RemoveRule, ListRules
  IGeoIpResolver        — Resolve(IPAddress) → CountryCode

Bad:
  INetworkService       — StartCapture, StopCapture, OnFlowEvent,
                          AddRule, RemoveRule, ListRules,
                          Resolve(IPAddress) → CountryCode
```

The UI depends on the gRPC protocol, not on the daemon's internal interfaces. The daemon's GeoIP resolver depends on `MaxMind.Db`, but nothing else in the daemon needs to know that library exists. Boundaries are tight.

### Dependency Inversion Principle

High-level modules do not depend on low-level modules. Both depend on abstractions.

```
Beholder.Daemon depends on IFlowSource     (defined in Core)
Beholder.Daemon.Windows provides EtwFlowSource (implements IFlowSource)

The daemon never references Beholder.Daemon.Windows directly in code.
The platform project is loaded at startup based on OS detection,
registered in DI, and consumed via the interface.
```

The same DIP principle governs the UI's platform abstraction, but at smaller scale: `INotificationService` is defined in `Beholder.Core`, `WindowsNotificationService` implements it inside `Beholder.Ui` itself (wrapped in `#if PLATFORM_WINDOWS`), and `App.axaml.cs` selects between it and `NoopNotificationService` at composition time via `OperatingSystem.IsWindows()`. The interface boundary is real; only the project boundary differs from the daemon's shape. [ADR 008](decisions/008-ui-single-project-policy.md) documents the rationale: the UI platform delta is small enough that source-level conditional compilation beats a separate project on overhead. The daemon-side split (`Beholder.Daemon.Windows`, `Beholder.Daemon.Linux`) stays mandatory because that delta is thousands of LOC across multiple OS subsystems.

This is not just a pattern for cross-platform code. It applies everywhere:

- Storage: the daemon depends on `IEventStore`, not on `SqliteEventStore` directly
- Logging: everything depends on `ILogger<T>`, never on a concrete logger
- Time: anything that needs the current time depends on `TimeProvider`, not `DateTime.UtcNow`

## DRY — Don't Repeat Yourself

Every piece of knowledge has a single, unambiguous, authoritative representation in the codebase.

### What DRY Means

- A business rule is defined once, in one place
- A data transformation is implemented once, called from wherever it's needed
- A constant is declared once, referenced by name everywhere else
- A validation check is written once in a shared method, not copy-pasted across handlers

### What DRY Does NOT Mean

DRY is about knowledge duplication, not code duplication. Two methods that happen to look similar but serve different purposes in different contexts are NOT duplicates. Forcing them to share code creates coupling that makes both harder to change independently.

```
Not duplication — these serve different purposes and will evolve independently:
  FormatBytesForUi(long bytes)        — human-friendly: "15.8 MB"
  FormatBytesForProtocol(long bytes)  — machine-friendly: exact long value

Actual duplication — same knowledge expressed twice:
  // In TrafficViewModel.cs
  if (bytes > 1_048_576) return $"{bytes / 1_048_576.0:F1}M";

  // In FirewallViewModel.cs (copy-pasted)
  if (bytes > 1_048_576) return $"{bytes / 1_048_576.0:F1}M";
```

When you spot actual duplication, extract it to the lowest common ancestor in the dependency graph. If both ViewModels need it, it goes in a shared utility in the UI project. If the daemon also needs it, it goes in Core.

## CLEAN Code

### Functions

- Do one thing. Do it completely. Do nothing else.
- Read top-to-bottom as a narrative. High-level methods call lower-level methods. The reader should never need to scroll up.
- Arguments: fewer is better. Zero is ideal, three is a warning, four is almost always a design problem. Group related parameters into a record.
- No side effects that aren't obvious from the name. `ResolveCountry` resolves a country. It does NOT also update a cache, log a metric, and trigger an alert. If it does, rename it or split it.
- No boolean flag arguments. `Process(connection, true)` is unreadable. Use two methods: `ProcessAllowed(connection)` and `ProcessBlocked(connection)`, or use an enum.

### Names

- Spend time choosing names. A good name saves hours of future confusion.
- A name should tell you why it exists, what it does, and how it is used
- Long, descriptive names are better than short, ambiguous names
- If you can't name it clearly, you don't understand what it does yet — design more before coding

### Error Handling

- Errors are not exceptional flow — they are part of the design. Handle them explicitly.
- Prefer returning discriminated results (`Result<T, E>` or a similar pattern) over throwing exceptions for expected failure modes (network timeout, file not found, parse failure).
- Reserve exceptions for truly exceptional situations: programmer errors (null where non-null was required), invariant violations, unrecoverable system failures.
- Every error path must be tested.

### Tests

- Tests are first-class code. They follow the same coding standards as production code.
- A test that is hard to read is a test that will be deleted or ignored when it fails.
- One logical assertion per test. Multiple `Assert` calls are fine if they assert different facets of the same logical outcome.
- Test behavior, not implementation. If refactoring internals breaks tests without changing behavior, the tests are coupled too tightly.

## Project-Specific Design Rules

### The Daemon's Hot Path

The flow capture → accumulation → IPC broadcast pipeline processes potentially thousands of events per second. In this pipeline:

- No allocations in the steady state. Reuse buffers. Pool objects.
- No LINQ in the hot path — it allocates enumerator objects. Use `for`/`foreach` over arrays or `Span<T>`.
- No `async` in the per-packet handler — the ETW callback is synchronous and must return fast. Offload processing to a `Channel<T>` that a background task drains.
- Profile before optimizing. Premature optimization is real. But known-hot paths get designed for performance from the start.

### The UI's Rendering Path

The Avalonia UI updates on a dispatcher thread. ViewModels push updates via property change notifications.

- Never block the UI thread. All IPC calls are async.
- Batch updates: the daemon sends counter deltas every second (configurable). The UI accumulates them in-memory and redraws on a timer tick, not on every incoming message.
- The traffic graph is a custom `Canvas`-drawn control, not a full charting library. It renders a fixed-width sliding window of data points. No unbounded lists.

### The Hash Chain

The hash chain is the project's core trust mechanism. It must be correct above all else.

- The chain writer is the ONLY code path that appends to the event log table. There is no "also write here" shortcut.
- The canonical byte representation of an event for hashing purposes is defined ONCE, in a static method on the event record, and NEVER reconstructed from stored fields.
- The chain is verified on daemon startup. If verification fails, the daemon logs a CHAIN alert and continues operating — it does not refuse to start, because refusing to start would prevent the user from investigating.
- Verification is idempotent and side-effect-free. Running it twice produces the same result. It modifies nothing.

### Configuration

- All configuration is read from `beholder.toml` at startup and injected via `IOptions<T>` / `IOptionsMonitor<T>`
- Secrets (signing keys, uplink tokens) are NEVER in the config file. They are in separate files referenced by path.
- Every config value has a sensible default. The daemon must start with ZERO user configuration.
- Config changes that affect the uplink or firewall behavior are logged as events in the chain.

## Decision Record

When you face a design decision with multiple valid approaches, record it. Create a short note in `docs/decisions/` with the format:

```
# NNN: Title

## Context
What is the problem?

## Decision
What did we choose?

## Consequences
What are the trade-offs?
```

This prevents re-litigating settled decisions and gives future contributors context for why things are the way they are.
