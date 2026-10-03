using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChatBridge;

/// <summary>
/// One live bridge process per state directory, and one per endpoint/room/nick on this host.
/// The kernel holds the lock until the last close of this file description, including a crash.
/// A pid written into the file is a hint for operators. Stop and status trust the kernel owner,
/// not that hint and not <c>state.json</c>.
/// </summary>
internal static class BridgeInstance
{
    public const int ExitAlreadyRunning = 4;
    public const string StateLockName = "bridge.instance.lock";

    /// <summary>
    /// Host-level identity locks. Same mount namespace only: a container with a private
    /// <c>/tmp</c> does not see the host's locks. The file name is a hash. The room, nick,
    /// trip, token, and hook secret are not part of the name.
    /// </summary>
    public const string IdentityDirectory = "/tmp/chatbridge-identity";

    public static string StateLockPath(string baseDir) =>
        Path.Combine(Path.GetFullPath(baseDir), StateLockName);

    public static string IdentityLockPath(RelayConfig cfg)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityMaterial(cfg))))
            .ToLowerInvariant();
        return Path.Combine(IdentityDirectory, "id-" + hash + ".lock");
    }

    /// <summary>
    /// Endpoint (no userinfo, query, or fragment), room, and nick. Trip, pass, and hook
    /// settings are not included, so they cannot change who the lock is for.
    /// </summary>
    internal static string IdentityMaterial(RelayConfig cfg) =>
        CanonicalEndpoint(cfg.Url) + "\n" + cfg.Channel + "\n" + cfg.Nick;

    internal static string CanonicalEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim();
        var builder = new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port, uri.AbsolutePath);
        var text = builder.Uri.GetComponents(
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path,
            UriFormat.UriEscaped);
        if (text.Length > 1 && text.EndsWith('/'))
            text = text.TrimEnd('/');
        return text;
    }

    public static void ReportAlreadyRunning(string kind, string path)
    {
        var probe = Probe(path);
        var who = probe.Pid is int pid ? $" (pid {pid})" : "";
        Console.Error.WriteLine($"[chatbridge] already running: another process holds the {kind} lock{who}");
    }

    public static InstanceProbe Probe(string path)
    {
        var full = Path.GetFullPath(path);
        if (Posix.IsSymlink(full))
            return new InstanceProbe(false, null);
        if (!File.Exists(full))
            return new InstanceProbe(false, null);

        var holder = Posix.QueryFlockHolder(full);
        if (holder.State == HolderState.Held)
            return new InstanceProbe(true, holder.Pid);
        if (holder.State == HolderState.NotHeld)
            return new InstanceProbe(false, null);

        // /proc/locks or stat was unreadable. A non-blocking take that we immediately drop
        // answers "is it free?" without staying the owner. Status uses this only then.
        if (InstanceFileLock.TryAcquire(full, InstanceLockKind.Probe) is { } taken)
        {
            taken.Dispose();
            return new InstanceProbe(false, null);
        }

        return new InstanceProbe(true, null);
    }

    public static string StatusLine(RelayConfig cfg)
    {
        var state = Probe(StateLockPath(cfg.BaseDir));
        if (state.Held)
            return state.Pid is int pid ? $"instance: running (pid {pid})" : "instance: running";

        var identity = Probe(IdentityLockPath(cfg));
        if (identity.Held)
        {
            return identity.Pid is int pid
                ? $"instance: host identity lock held (pid {pid})"
                : "instance: host identity lock held";
        }

        return "instance: not running";
    }

    /// <summary>
    /// Signal the process that currently holds this config's state lock.
    /// A connected socket is not a reason to skip the signal.
    /// </summary>
    public static InstanceStop Stop(RelayConfig cfg)
    {
        var statePath = StateLockPath(cfg.BaseDir);
        var state = Probe(statePath);
        if (!state.Held)
        {
            var identity = Probe(IdentityLockPath(cfg));
            if (identity.Held)
            {
                var who = identity.Pid is int pid ? $"pid {pid}" : "an unverified process";
                return InstanceStop.Block(
                    $"[chatbridge] stop: this state directory is free, but the host identity lock is held by {who}. Refusing to signal it.");
            }

            return InstanceStop.Absent("[chatbridge] stop: not running");
        }

        if (state.Pid is not int owner || owner <= 1 || owner == Environment.ProcessId)
            return InstanceStop.Block("[chatbridge] stop: the state lock is held, but the owner could not be verified");

        if (!Posix.SignalTerm(owner))
        {
            if (!Probe(statePath).Held)
                return InstanceStop.Stopped($"[chatbridge] stopped pid {owner}");
            return InstanceStop.Block("[chatbridge] stop: could not signal the owner");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!Probe(statePath).Held)
                return InstanceStop.Stopped($"[chatbridge] stopped pid {owner}");
            Thread.Sleep(50);
        }

        return InstanceStop.Block($"[chatbridge] stop: pid {owner} was signaled and the state lock is still held");
    }
}

internal enum InstanceStopKind
{
    Stopped,
    Absent,
    Blocked
}

internal readonly record struct InstanceStop(InstanceStopKind Kind, string Detail)
{
    public int Code => Kind == InstanceStopKind.Stopped ? 0 : 1;

    /// <summary>Restart may become the new owner only after a real stop, or when nothing was running.</summary>
    public bool MayStart => Kind is InstanceStopKind.Stopped or InstanceStopKind.Absent;

    public static InstanceStop Stopped(string detail) => new(InstanceStopKind.Stopped, detail);
    public static InstanceStop Absent(string detail) => new(InstanceStopKind.Absent, detail);
    public static InstanceStop Block(string detail) => new(InstanceStopKind.Blocked, detail);
}

internal readonly record struct InstanceProbe(bool Held, int? Pid);

internal enum InstanceLockKind
{
    State,
    Identity,
    /// <summary>Non-creating probe. Does not chmod and does not write a pid.</summary>
    Probe
}

/// <summary>
/// Exclusive <c>flock</c> on one open file description, closed when the process exits.
/// <see cref="FileShare.None"/> is not used: readers (status) must be able to open the file
/// without becoming the owner.
/// </summary>
internal sealed class InstanceFileLock : IDisposable
{
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private InstanceFileLock(SafeFileHandle handle) => _handle = handle;

    /// <summary>Null when another owner already holds the lock. Setup failures throw <see cref="ConfigException"/>.</summary>
    public static InstanceFileLock? TryAcquire(string path, InstanceLockKind kind)
    {
        var full = Path.GetFullPath(path);
        if (Posix.IsSymlink(full))
            throw new ConfigException("instance lock path is a symlink");

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
            throw new ConfigException("instance lock path has no directory");

        if (kind != InstanceLockKind.Probe)
        {
            Directory.CreateDirectory(parent);
            if (kind == InstanceLockKind.Identity)
                Posix.MakeStickyPublic(parent);
        }
        else if (!File.Exists(full))
        {
            return null;
        }

        var fd = Posix.Open(full, create: kind != InstanceLockKind.Probe);
        if (fd < 0)
        {
            var err = MarshalLast();
            if (err == Posix.ELoop)
                throw new ConfigException("instance lock path is a symlink");
            if (err == Posix.ENoEnt && kind == InstanceLockKind.Probe)
                return null;
            throw new ConfigException($"instance lock cannot be opened ({Posix.ErrnoName(err)})");
        }

        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        Posix.EnsureCloexec(handle);
        if (kind == InstanceLockKind.Identity)
            Posix.MakeWorldLockable(handle);

        if (!Posix.TryLockExclusive(handle))
        {
            handle.Dispose();
            return null;
        }

        if (kind != InstanceLockKind.Probe)
            Posix.WritePidHint(handle);
        return new InstanceFileLock(handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { Posix.Unlock(_handle); }
        catch { /* closing the fd releases the flock anyway */ }
        try { _handle.Dispose(); }
        catch { /* already closed */ }
    }

    private static int MarshalLast() => Marshal.GetLastWin32Error();
}

internal enum HolderState
{
    NotHeld,
    Held,
    Unknown
}

internal readonly record struct HolderQuery(HolderState State, int? Pid);

/// <summary>Linux flock plus <c>/proc/locks</c>. Owner checks do not trust a pid file.</summary>
internal static class Posix
{
    internal const int ELoop = 40;
    internal const int ENoEnt = 2;
    private const int EAgain = 11;
    private const int EIntr = 4;
    private const int ESrch = 3;
    private const int EAcces = 13;

    private const int ORdwr = 2;
    private const int OCreat = 64;
    private const int ONoFollow = 0x20000;
    private const int OCloexec = 0x80000;

    private const int LockEx = 2;
    private const int LockNb = 4;
    private const int LockUn = 8;

    private const int FGetFd = 1;
    private const int FSetFd = 2;
    private const int FdCloexec = 1;

    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int StatxBasic = 0x7ff;
    private const int SigTerm = 15;

    private const ushort SIfmt = 0xF000;
    private const ushort SIflnk = 0xA000;

    public static int Open(string path, bool create)
    {
        var flags = ORdwr | ONoFollow | OCloexec;
        if (create)
            flags |= OCreat;
        return sys_open(path, flags, create ? 0x1A4 : 0); // 0644; identity locks are chmod'd after
    }

    public static bool TryLockExclusive(SafeFileHandle handle)
    {
        while (true)
        {
            if (sys_flock(handle, LockEx | LockNb) == 0)
                return true;
            var err = Errno();
            if (err == EIntr)
                continue;
            if (err == EAgain)
                return false;
            throw new ConfigException($"instance lock cannot be taken ({ErrnoName(err)})");
        }
    }

    public static void Unlock(SafeFileHandle handle)
    {
        if (!handle.IsInvalid && !handle.IsClosed)
            sys_flock(handle, LockUn);
    }

    public static void EnsureCloexec(SafeFileHandle handle)
    {
        var flags = sys_fcntl_get(handle, FGetFd);
        if (flags >= 0 && (flags & FdCloexec) == 0)
            sys_fcntl_set(handle, FSetFd, flags | FdCloexec);
    }

    public static void MakeWorldLockable(SafeFileHandle handle) => sys_fchmod(handle, 0x1B6); // 0666

    public static void MakeStickyPublic(string directory)
    {
        // 01777: sticky + rwx for everyone, so two users on this host contend for one identity file.
        sys_chmod(directory, 0x3FF);
    }

    public static void WritePidHint(SafeFileHandle handle)
    {
        var text = Encoding.ASCII.GetBytes($"pid={Environment.ProcessId}\n");
        if (sys_ftruncate(handle, 0) != 0)
            return;
        sys_lseek(handle, 0, 0);
        var wrote = sys_write(handle, text, (nuint)text.Length);
        if (wrote == text.Length)
            sys_fsync(handle);
    }

    public static bool IsSymlink(string path)
    {
        if (!TryStat(path, out var mode, out _, out _, out _))
            return false;
        return (mode & SIfmt) == SIflnk;
    }

    public static HolderQuery QueryFlockHolder(string path)
    {
        if (!TryStat(path, out _, out var major, out var minor, out var ino))
            return new HolderQuery(HolderState.Unknown, null);

        string text;
        try
        {
            text = File.ReadAllText("/proc/locks");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HolderQuery(HolderState.Unknown, null);
        }

        var needle = $"{major:x2}:{minor:x2}:{ino}";
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
                continue;
            if (!parts[1].Equals("FLOCK", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!parts[3].Equals("WRITE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!parts[5].Equals(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(parts[4], out var pid) && pid > 0)
                return new HolderQuery(HolderState.Held, pid);
        }

        return new HolderQuery(HolderState.NotHeld, null);
    }

    public static bool SignalTerm(int pid)
    {
        if (pid <= 1 || pid == Environment.ProcessId)
            return false;
        if (sys_kill(pid, SigTerm) == 0)
            return true;
        return Errno() == ESrch;
    }

    public static string ErrnoName(int err) => err switch
    {
        EAgain => "busy",
        ENoEnt => "not found",
        EAcces => "access denied",
        ELoop => "symlink",
        _ => "errno " + err
    };

    private static bool TryStat(string path, out ushort mode, out uint major, out uint minor, out ulong ino)
    {
        mode = 0;
        major = 0;
        minor = 0;
        ino = 0;
        var buf = new byte[256];
        var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (sys_statx(AtFdcwd, path, AtSymlinkNoFollow, StatxBasic, pinned.AddrOfPinnedObject()) != 0)
                return false;
        }
        finally
        {
            pinned.Free();
        }

        mode = BitConverter.ToUInt16(buf, 0x1C);
        ino = BitConverter.ToUInt64(buf, 0x20);
        major = BitConverter.ToUInt32(buf, 0x88);
        minor = BitConverter.ToUInt32(buf, 0x8C);
        return true;
    }

    private static int Errno() => Marshal.GetLastWin32Error();

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int sys_open(string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int sys_flock(SafeFileHandle fd, int operation);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int sys_fcntl_get(SafeFileHandle fd, int cmd);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int sys_fcntl_set(SafeFileHandle fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int sys_fchmod(SafeFileHandle fd, uint mode);

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int sys_chmod(string path, uint mode);

    [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static extern int sys_ftruncate(SafeFileHandle fd, long length);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long sys_lseek(SafeFileHandle fd, long offset, int whence);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern long sys_write(SafeFileHandle fd, byte[] buf, nuint count);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int sys_fsync(SafeFileHandle fd);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int sys_statx(int dirfd, string pathname, int flags, int mask, IntPtr buf);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);
}
