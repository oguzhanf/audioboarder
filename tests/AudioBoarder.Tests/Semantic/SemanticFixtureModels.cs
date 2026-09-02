using System.Text.Json;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Semantic;

public sealed class SemanticFixtureDocument
{
    public int SchemaVersion { get; init; }
    public required IReadOnlyList<SemanticGoldenCase> Cases { get; init; }
}

public sealed class SemanticGoldenCase
{
    public required string Id { get; init; }
    public required string Intent { get; init; }
    public required string Scenario { get; init; }
    public required string Transcript { get; init; }
    public required SemanticExpectations Expected { get; init; }
    public required JsonElement CapturedPatch { get; init; }
    public string? Notes { get; init; }
}

public sealed class SemanticExpectations
{
    public required IReadOnlyList<NodeExpectation> RequiredNodes { get; init; }
    public IReadOnlyList<NodeExpectation> OptionalNodes { get; init; } = [];
    public IReadOnlyList<ForbiddenNodeExpectation> ForbiddenNodes { get; init; } = [];
    public IReadOnlyList<ForbiddenFactExpectation> ForbiddenFacts { get; init; } = [];
    public required IReadOnlyList<InteractionExpectation> Interactions { get; init; }
    public required IReadOnlyList<GroupExpectation> Groups { get; init; }
    public IReadOnlyList<NoteExpectation> Notes { get; init; } = [];
}

public sealed class NodeExpectation
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required NodeKind Kind { get; init; }
}

public sealed class ForbiddenNodeExpectation
{
    public required string Label { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed class InteractionExpectation
{
    public required string From { get; init; }
    public required string To { get; init; }
    public EdgeKind Kind { get; init; } = EdgeKind.Flow;
    public IReadOnlyList<string> LabelTerms { get; init; } = [];
    public int? Step { get; init; }
}

public sealed class GroupExpectation
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required IReadOnlyList<string> Members { get; init; }
    public string? Parent { get; init; }
}

public sealed class NoteExpectation
{
    public required NoteKind Kind { get; init; }
    public string? Owner { get; init; }
    public required IReadOnlyList<string> TextTerms { get; init; }
}

public sealed class ForbiddenFactExpectation
{
    public required string Kind { get; init; }
    public required string Term { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
}
