using System;

namespace Beholder.Core;

/// <summary>
/// Request shape for <see cref="ITrafficStore.GetDestinationsAsync"/>. Grouped
/// into a record per PRINCIPLES.md §Functions ("Arguments: fewer is better.
/// Zero is ideal, three is a warning, four is almost always a design problem.
/// Group related parameters into a record."). The five fields are tightly
/// correlated — they all parameterize the same SQL aggregation.
/// </summary>
/// <param name="ProcessPath">
/// When null, aggregates across every process in the range; when non-null,
/// restricts to flows where <c>process_path = ProcessPath</c>.
/// </param>
/// <param name="From">Inclusive lower bound on the bucket timestamp.</param>
/// <param name="To">Exclusive upper bound on the bucket timestamp.</param>
/// <param name="Country">
/// When null, no country filter (existing pre-Phase-8 behavior); when set
/// to an ISO 3166-1 alpha-2 code, restricts to flows where <c>country =
/// Country</c>. Added in Phase 8 polish for the world-map hover tooltip's
/// per-country top-N drill-down.
/// </param>
/// <param name="Limit">
/// When 0 or negative, returns all destinations (existing pre-Phase-8
/// behavior). When positive, returns only the top <paramref name="Limit"/>
/// destinations after the ORDER BY total bytes DESC. Added in Phase 8
/// polish so the map hover can fetch just the top-3 cheaply.
/// </param>
public sealed record DestinationsQuery(
    string? ProcessPath,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Country,
    int Limit);
