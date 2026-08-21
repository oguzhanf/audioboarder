namespace AudioBoarder.Core.Scene;

/// <summary>
/// Visual shape category for a <see cref="SceneNode"/>.
/// The LLM picks the kind; the renderer maps each kind to a Skia primitive
/// and the Excalidraw converter maps it to a shape + colour + default glyph.
/// </summary>
public enum NodeKind
{
    Process,
    Entity,
    Decision,
    DataStore,
    Actor,
    Note,

    /// <summary>A bounded system or platform. Rendered as a heavy-bordered container-style box.</summary>
    System,

    /// <summary>A named product/technology (Power BI, Purview, Fabric...). Carries a product glyph.</summary>
    Technology,

    /// <summary>A control, policy, or protective capability.</summary>
    Security,

    /// <summary>A cloud service or hosted platform boundary.</summary>
    Cloud,

    /// <summary>A report, document, artifact, or deliverable.</summary>
    Document,

    /// <summary>A milestone, phase, or point in a timeline.</summary>
    Milestone,

    /// <summary>A stated risk, gap, or concern raised in the discussion.</summary>
    Risk,

    /// <summary>A measure, KPI, volume, or quantity.</summary>
    Metric,

    /// <summary>An external party, third-party system, or out-of-scope actor.</summary>
    External,

    /// <summary>An on-canvas explanation attached beside the thing it explains.</summary>
    Callout,
}
