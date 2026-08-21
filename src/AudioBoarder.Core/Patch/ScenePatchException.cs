namespace AudioBoarder.Core.Patch;

public sealed class ScenePatchException : Exception
{
    public int OperationIndex { get; }
    public string? OperationName { get; }

    public ScenePatchException(int index, string? opName, string message)
        : base($"ScenePatch op #{index} ({opName ?? "?"}): {message}")
    {
        OperationIndex = index;
        OperationName = opName;
    }
}
