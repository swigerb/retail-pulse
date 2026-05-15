using BenchmarkDotNet.Running;

internal class BenchmarkEntry
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEntry).Assembly).Run(args);
}
