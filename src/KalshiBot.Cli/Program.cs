using KalshiBot.Cli;
using KalshiBot.Core.Common;

try
{
    return await Commands.RunAsync(args);
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine("run 'kb help' for usage");
    return 2;
}
catch (KalshiBotException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
