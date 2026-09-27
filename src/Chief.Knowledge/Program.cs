namespace Chief.Knowledge;

public sealed class KnowledgeException : Exception
{
    public KnowledgeException(int exitCode, string message) : base(message) => ExitCode = exitCode;

    public int ExitCode { get; }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return KnowledgeCli.Run(args, Console.Out, Console.Error);
        }
        catch (Exception ex) when (ex is not KnowledgeException)
        {
            Console.Error.WriteLine($"[knowledge] {ex.Message}");
            return 2;
        }
    }
}
