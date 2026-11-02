namespace Reconciliation.Tests;

internal static class RepoPaths
{
    public static string Root => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    public static string Sample(string file) => Path.Combine(Root, "samples", file);
    public static string Doc(string file) => Path.Combine(Root, "docs", file);
}
