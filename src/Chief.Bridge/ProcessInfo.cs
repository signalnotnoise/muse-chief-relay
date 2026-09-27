using System.Diagnostics;

namespace Chief.Bridge;

internal static class ProcessInfo
{
    public static bool IsRunning(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
