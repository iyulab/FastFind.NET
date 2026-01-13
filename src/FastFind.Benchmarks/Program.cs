using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Loggers;
using FastFind.Windows;

namespace FastFind.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        // Ensure Windows factory is registered
        if (OperatingSystem.IsWindows())
        {
            WindowsRegistration.EnsureRegistered();
        }

        var config = DefaultConfig.Instance
            .WithArtifactsPath("./Results")
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .AddColumn(StatisticColumn.Mean)
            .AddColumn(StatisticColumn.StdDev)
            .AddColumn(StatisticColumn.Median)
            .AddColumn(RankColumn.Arabic)
            .AddLogger(ConsoleLogger.Default);

        // Parse command line for filter
        if (args.Length > 0 && args[0] == "--filter")
        {
            var filter = args.Length > 1 ? args[1] : "*";
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(["--filter", filter], config);
        }
        else if (args.Length > 0 && args[0] == "--list")
        {
            // List all available benchmarks
            Console.WriteLine("Available Benchmarks:");
            Console.WriteLine("=====================");
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(BenchmarkDotNet.Attributes.BenchmarkAttribute), false).Any()))
                .OrderBy(t => t.Name);

            foreach (var type in types)
            {
                Console.WriteLine($"  - {type.Name}");
                var methods = type.GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(BenchmarkDotNet.Attributes.BenchmarkAttribute), false).Any());
                foreach (var method in methods)
                {
                    Console.WriteLine($"      * {method.Name}");
                }
            }
        }
        else if (args.Length > 0)
        {
            // Run with arguments
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, config);
        }
        else
        {
            // Run all benchmarks
            Console.WriteLine("Running all benchmarks...");
            Console.WriteLine("Use --filter <pattern> to run specific benchmarks");
            Console.WriteLine("Use --list to list available benchmarks");
            Console.WriteLine();

            BenchmarkRunner.Run(typeof(Program).Assembly, config);
        }
    }
}
