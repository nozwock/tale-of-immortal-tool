namespace TaleOfImmortalTool;

public static class ModTool
{
    /// <summary>
    /// ModTool$$RandomID
    /// </summary>
    public static int RandomId()
        => Random.Shared.Next(1, 42) > 20
            ? Random.Shared.Next(int.MinValue, -100_000_1)
            : Random.Shared.Next(100_000_001, int.MaxValue);
}