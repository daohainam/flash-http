using BenchmarkDotNet.Running;

namespace BenchmarkSuite
{
    internal class Program
    {
        // No arguments: run everything with the default (slow, accurate) job. With arguments:
        // standard BenchmarkDotNet switches, e.g. a quick pass while iterating:
        //   --filter *Loopback* --inProcess --launchCount 1 --warmupCount 3 --iterationCount 6
        static void Main(string[] args)
        {
            var assembly = typeof(Program).Assembly;

            if (args.Length == 0)
                BenchmarkRunner.Run(assembly);
            else
                BenchmarkSwitcher.FromAssembly(assembly).Run(args);
        }
    }
}
