# Coding Standards

This document defines the formatting, naming, and structural conventions for all C# code in Beholder NMT. These are not suggestions. All code — new, modified, or refactored — must conform before merge.

## Formatting

### Braces

Opening braces go on the SAME line as the statement. This is non-negotiable.

```csharp
// Correct
if (condition) {
    DoSomething();
}

public class TrafficMonitor {
    public void Start() {
        // ...
    }
}

// Wrong — never Allman style
if (condition)
{
    DoSomething();
}
```

### Single-Line Bodies

If an `if`, `else`, `foreach`, `while`, or `for` has a single-expression body, write it on one line with no braces. If it doesn't fit on one line (~120 chars), add braces and break it.

```csharp
// Correct — single expression, fits on one line
if (process is null) return;
if (bytes == 0) continue;
foreach (var rule in rules) ApplyRule(rule);

// Correct — too long for one line, use braces
if (connection.RemoteEndPoint.Address.IsPrivate()) {
    cache.Set(connection.RemoteEndPoint.Address, "LAN");
}

// Wrong — braces on a single short statement
if (process is null) {
    return;
}

// Wrong — multi-line body without braces
if (condition)
    DoFirst();
    DoSecond(); // this is NOT inside the if — classic bug
```

If an `if` has an `else`, both branches get braces regardless of length. No mixing.

```csharp
// Correct
if (isBlocked) {
    LogBlocked(connection);
} else {
    Forward(connection);
}

// Wrong — mixed braces
if (isBlocked) {
    LogBlocked(connection);
} else
    Forward(connection);
```

### Indentation and Spacing

- 4 spaces, no tabs. Configure your editor.
- One blank line between methods. No blank lines at the start or end of a block.
- No trailing whitespace.
- Files end with a single newline.
- Maximum line length: 120 characters. Prefer shorter.

### Expression Bodies

Use expression-bodied members for single-expression methods, properties, and constructors.

```csharp
// Correct
public string Name => _name;
public override string ToString() => $"{ProcessName} ({Pid})";
public int Count => _items.Count;

// Also correct — too complex for expression body, use block
public decimal CalculateRate() {
    var elapsed = _stopwatch.Elapsed.TotalSeconds;
    if (elapsed == 0) return 0;
    return _totalBytes / (decimal)elapsed;
}
```

### `var` Usage

Use `var` when the type is obvious from the right side. Use explicit types when it isn't.

```csharp
// Correct — type is obvious
var cache = new Dictionary<IPAddress, string>();
var connection = GetConnection();  // if GetConnection clearly returns Connection
var count = items.Count;           // clearly an int

// Correct — type is not obvious, spell it out
IFlowSource source = CreatePlatformSource();
CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(a, b);
```

### `using` Directives

- Global usings in a single `GlobalUsings.cs` file per project for types used everywhere
- No `using` aliases except to resolve ambiguity
- Sort: System namespaces first, then third-party, then project namespaces
- Remove all unused usings (IDE should enforce this)

## Naming

### General Rules

| Element            | Convention      | Example                        |
|--------------------|-----------------|--------------------------------|
| Namespace          | PascalCase      | `Beholder.Daemon.GeoIp`       |
| Class / Struct     | PascalCase      | `TrafficMonitor`               |
| Interface          | IPascalCase     | `IFlowSource`                  |
| Method             | PascalCase      | `ResolveCountry`               |
| Property           | PascalCase      | `BytesOut`                     |
| Event              | PascalCase      | `ConnectionBlocked`            |
| Enum type          | PascalCase      | `AlertKind`                    |
| Enum value         | PascalCase      | `AlertKind.NewProcess`         |
| Local variable     | camelCase       | `packetCount`                  |
| Parameter          | camelCase       | `remoteAddress`                |
| Private field      | _camelCase      | `_connectionCache`             |
| Constant           | PascalCase      | `MaxRetryCount`                |
| Type parameter     | T + PascalCase  | `TMessage`, `TResult`          |

### Naming Principles

- Names describe WHAT something IS or DOES, not HOW it does it
- Boolean names read as questions: `IsBlocked`, `HasExpired`, `CanConnect`
- Collection names are plural: `rules`, `connections`, `alerts`
- Method names are verbs: `Resolve`, `Block`, `Emit`, `Verify`
- No Hungarian notation: never `strName`, `iCount`, `bIsValid`
- No meaningless names: never `data`, `info`, `item`, `temp`, `result`, `value` as standalone names (fine as part of a compound name like `geoResult` or `counterValue`)
- Acronyms of 2 letters are all caps (`IP`, `UI`, `DB`), 3+ letters are PascalCase (`Tcp`, `Asn`, `Geo`)

### File Naming

- One public type per file
- File name matches the type name: `TrafficMonitor.cs` contains `class TrafficMonitor`
- Interfaces get their own file: `IFlowSource.cs`
- Nested private types stay in the parent's file

### File Organization Within a Class

Members are ordered top-to-bottom in this sequence:

1. Constants and static readonly fields
2. Private fields
3. Constructor(s)
4. Public properties
5. Public methods
6. Internal/protected methods
7. Private methods
8. Nested types

Within each group, order by logical cohesion (related things together), not alphabetically.

## Comments

### Rules

- Comments explain WHY, never WHAT or HOW
- If you feel the need to comment WHAT code does, the code is not clear enough — rename, extract, simplify
- No banner comments (`// ========== SECTION ==========`)
- No region directives (`#region`). Ever. They hide code and encourage bloated classes.
- No journal comments (`// Added by John on 2024-01-15`). That is what Git is for.
- No noise comments (`// Constructor`, `// Gets the name`, `// Loop through items`)
- No commented-out code. Delete it. Git remembers.

### When Comments ARE Appropriate

```csharp
// Ed25519 chosen over RSA because: 32-byte keys, sub-ms signing, deterministic
// output. RSA would require 2048+ bit keys and non-deterministic padding.
private readonly Ed25519Signer _signer;

// DB-IP returns alpha-2 (US), LiveCharts2 expects lowercase alpha-3 (usa).
// RegionInfo handles the conversion without a lookup table.
var alpha3 = new RegionInfo(countryCode).ThreeLetterISORegionName.ToLowerInvariant();

// RFC 1918 / RFC 4193 / RFC 5737 ranges — no point hitting the MMDB for these
if (address.IsPrivateOrReserved()) return CountryCode.Local;
```

### XML Documentation

Required on all public types and public members in `Beholder.Core` and `Beholder.Protocol` because these are consumed by other projects. Not required on private/internal members or on UI view models.

```csharp
/// <summary>
/// Resolves an IP address to its ISO 3166-1 alpha-2 country code using the DB-IP MMDB.
/// Returns <see cref="CountryCode.Unknown"/> for addresses not found in the database.
/// Returns <see cref="CountryCode.Local"/> for private/reserved ranges without hitting the MMDB.
/// </summary>
public CountryCode Resolve(IPAddress address);
```

Keep XML docs factual and concise. No filler phrases like "This method is used to..." — just say what it does.

## Patterns and Practices

### Dependency Injection

- Constructor injection only. No property injection, no service locator, no `static` singletons.
- Interfaces are registered in `Beholder.Daemon/Program.cs` or the UI's composition root.
- Never `new` up a service class inside another service class — inject the dependency.

### Async

- All I/O-bound operations are async. No blocking calls (`Task.Result`, `Task.Wait()`, `.GetAwaiter().GetResult()`).
- Suffix async methods with `Async` only if a synchronous overload also exists. Otherwise, just name it naturally: `Resolve`, not `ResolveAsync`, if there's no sync variant.
- Always pass and honor `CancellationToken`.
- Use `ValueTask<T>` for hot paths that often complete synchronously (e.g., cache hits).

### Error Handling

- Catch specific exceptions, not `Exception` (except at the outermost boundary: the host process).
- Never swallow exceptions silently. At minimum, log them.
- Use `ILogger<T>` for all logging. No `Console.Write`. No `Debug.Write`.
- Structured logging: `_logger.LogWarning("Failed to resolve {Address}: {Reason}", ip, ex.Message)` — never string concatenation or interpolation in log templates.

### Records and Immutability

- Use `record` or `record struct` for data transfer objects, events, and messages.
- Prefer `init` properties over mutable setters for configuration objects.
- Collections exposed as properties should be `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, or `ImmutableArray<T>` — never `List<T>` or `Dictionary<K,V>`.

### Enums

- No `[Flags]` unless the values are genuinely combinable bitmasks
- Provide an explicit `Unknown = 0` or `None = 0` first value where applicable
- Never cast integers to enums without validation

## Banned Patterns

These patterns are explicitly forbidden. If you find yourself reaching for one, stop and reconsider.

| Pattern | Why | Instead |
|---------|-----|---------|
| `#region` | Hides complexity; encourages bloated files | Split into smaller classes |
| `dynamic` | Loses all type safety | Use generics or interfaces |
| `Thread.Sleep` | Blocks a thread | `await Task.Delay()` |
| `Task.Result` / `Task.Wait()` | Deadlock risk | `await` |
| Service locator (`provider.GetService<T>()` in business logic) | Hidden dependencies | Constructor injection |
| Singleton via `static` | Untestable, hidden coupling | Register as singleton in DI |
| `string.Format` | Easy to get indices wrong | String interpolation `$""` |
| Magic numbers / strings | Unclear intent | Named constants or enums |
| `catch (Exception) { }` | Swallows all errors | Catch specific, log, rethrow or handle |
| `public` fields | No encapsulation | Properties |
| Mutable static state | Thread-unsafe, test-hostile | Injected services with scoped lifetime |
| `goto` | Unstructured flow | Restructure the logic |
| Nested ternaries | Unreadable | `if`/`else` or `switch` expression |
