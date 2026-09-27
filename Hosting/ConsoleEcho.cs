using SampleLogGenerator.Models;
using SampleLogGenerator.Simulation;

namespace SampleLogGenerator.Hosting;

/// <summary>Prints log lines in a compact, colour-coded text format so you can watch traffic live.</summary>
internal static class ConsoleEcho
{
    public static void Write(IEnumerable<LogEntry> entries)
    {
        foreach (var e in entries)
        {
            Console.ForegroundColor = e.Level switch
            {
                Levels.Warning => ConsoleColor.Yellow,
                Levels.Error => ConsoleColor.Red,
                Levels.Critical => ConsoleColor.Magenta,
                _ => ConsoleColor.Gray,
            };
            var order = e.OrderId is null ? "" : $" [{e.OrderId}]";
            Console.WriteLine($"{e.Timestamp:HH:mm:ss.fff} {e.Level,-11} {e.Service,-19}{order} {e.Message}");
        }
        Console.ResetColor();
    }
}
