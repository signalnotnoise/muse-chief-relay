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
        if (holder.State == HolderState.NotHeld)
            return new InstanceProbe(false, null);
        // A verified pid is only returned together with Held when the device id or the
        // live descriptor's inode matched this file. Held without a pid means the kernel
        // probe found the lock and the owner could not be verified. Stop does not signal that.
        if (holder.State == HolderState.Held && holder.Pid is int verified)
            return new InstanceProbe(true, verified);

        // Readable /proc/locks that did not match, a permission-denied fd table, or a
        // device id that is not this file. The non-blocking take decides whether THIS
        // inode is held. It does not stay the owner and it does not invent a pid.
        if (InstanceFileLock.TryAcquire(full, InstanceLockKind.Probe) is { } taken)
        {
            taken.Dispose();
            return new InstanceProbe(false, null);
        }

        return new InstanceProbe(true, Posix.DiscoverOwnerPid(full));
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

        // A /proc/locks pid is not enough. The process must still have a descriptor
        // for this file. A missing pid, or a descriptor that was deleted or replaced,
        // is not signaled.
        if (state.Pid is not int owner || owner <= 1 || owner == Environment.ProcessId
            || !Posix.OwnerDescriptorRefersTo(statePath, owner))
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

    internal bool CloseOnExec => Posix.CloseOnExec(_handle);

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
        try { Posix.EnsureCloexec(handle); }
        catch { handle.Dispose(); throw; }
        if (kind == InstanceLockKind.Identity)
            Posix.MakeWorldLockable(handle);

        try
        {
            if (!Posix.TryLockExclusive(handle))
            {
                handle.Dispose();
                return null;
            }
        }
        catch { handle.Dispose(); throw; }

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

/// <summary>Linux flock/proc locks or macOS confined OFD locks. Owner checks do not trust a pid file.</summary>
internal static class Posix
{
    /// <summary>
    /// Linux owner source. Tests point this at a missing file on this thread to prove the
    /// fd-table fallback still names the kernel owner and still ignores a stale pid hint.
    /// </summary>
    [ThreadStatic]
    private static string? _procLocksOverride;

    internal static string ProcLocksPath
    {
        get => _procLocksOverride ?? "/proc/locks";
        set => _procLocksOverride = value;
    }

    /// <summary>
    /// Tests point this at a mount table where a longer path prefix is a different device.
    /// A known statx mount id must not adopt that device.
    /// </summary>
    [ThreadStatic]
    private static string? _mountInfoOverride;

    internal static string MountInfoPath
    {
        get => _mountInfoOverride ?? "/proc/self/mountinfo";
        set => _mountInfoOverride = value;
    }

    /// <summary>
    /// When set, the statx device id is replaced for the /proc/locks needle only.
    /// Mountinfo keeps the device id the kernel actually prints. Tests use this to
    /// reproduce an overlay host where stx_dev_minor and the locks minor differ.
    /// </summary>
    [ThreadStatic]
    private static uint? _statMajorOverride;

    [ThreadStatic]
    private static uint? _statMinorOverride;

    internal static void SetStatDeviceOverride(uint? major, uint? minor)
    {
        _statMajorOverride = major;
        _statMinorOverride = minor;
    }

    /// <summary>
    /// Tests hide <c>fdinfo</c> so a readable <c>/proc/locks</c> miss cannot borrow a pid
    /// from the fd table. The non-blocking probe still decides whether the file is held.
    /// </summary>
    [ThreadStatic]
    private static bool _hideFdInfo;

    internal static void SetHideFdInfo(bool hide) => _hideFdInfo = hide;

    internal static int ELoop => OperatingSystem.IsMacOS() ? 62 : 40;
    internal const int ENoEnt = 2;
    private static int EAgain => OperatingSystem.IsMacOS() ? 35 : 11;
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
    private const int StatxMntId = 0x1000;
    private const int SigTerm = 15;

    private const ushort SIfmt = 0xF000;
    private const ushort SIflnk = 0xA000;

    internal static void SyncDirectory(string path)
    {
        // fsync the rename's containing directory so power loss cannot lose a committed queue state.
        var fd = sys_open(path, OperatingSystem.IsMacOS() ? 0x01000000 : 0x80000, 0);
        if (fd < 0) throw new IOException("queue directory cannot be opened for sync");
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (sys_fsync(handle) != 0) throw new IOException("queue directory cannot be synced");
    }

    public static int Open(string path, bool create)
    {
        var flags = OperatingSystem.IsMacOS() ? ORdwr | 0x100 | 0x01000000 : ORdwr | ONoFollow | OCloexec;
        if (create)
            flags |= OperatingSystem.IsMacOS() ? 0x200 : OCreat;
        return sys_open(path, flags, create ? 0x1A4 : 0); // 0644; identity locks are chmod'd after
    }

    public static bool TryLockExclusive(SafeFileHandle handle)
    {
        while (true)
        {
            var record = new DarwinLock { Type = 3 }; // F_WRLCK, entire file
            if ((OperatingSystem.IsMacOS()
                ? sys_fcntl_lock(handle, 90, ref record) // F_OFD_SETLK
                : sys_flock(handle, LockEx | LockNb)) == 0)
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
        {
            if (OperatingSystem.IsMacOS())
            {
                var record = new DarwinLock { Type = 2 }; // F_UNLCK
                sys_fcntl_lock(handle, 90, ref record);
            }
            else sys_flock(handle, LockUn);
        }
    }

    internal static bool CloseOnExec(SafeFileHandle handle) =>
        sys_fcntl_get(handle, FGetFd) is var flags && flags >= 0 && (flags & FdCloexec) != 0;

    public static void EnsureCloexec(SafeFileHandle handle)
    {
        var flags = sys_fcntl_get(handle, FGetFd);
        var required = OperatingSystem.IsMacOS() ? 3 : FdCloexec; // CLOEXEC + CLOFORK on Darwin
        if (flags < 0 || sys_fcntl_set(handle, FSetFd, flags | required) != 0)
            throw new ConfigException("instance lock cannot be marked close-on-exec");
        // A confined OFD cannot be transferred to another process. Darwin can therefore
        // report its kernel owner PID through F_OFD_GETLK, without trusting the pid hint.
        if (OperatingSystem.IsMacOS() && sys_fcntl_set(handle, 95, 1) != 0) // F_SETCONFINED
            throw new ConfigException("instance lock cannot be confined to this macOS process");
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
        if (OperatingSystem.IsMacOS())
            return new FileInfo(path).LinkTarget is not null;
        if (!TryStat(path, out var mode, out _, out _, out _, out _))
            return false;
        return (mode & SIfmt) == SIflnk;
    }

    public static HolderQuery QueryFlockHolder(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var fd = Open(path, create: false);
            if (fd < 0) return new HolderQuery(HolderState.Unknown, null);
            using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            var record = new DarwinLock { Type = 3 };
            if (sys_fcntl_lock(handle, 92, ref record) != 0) // F_OFD_GETLK
                return new HolderQuery(HolderState.Unknown, null);
            return record.Type == 2
                ? new HolderQuery(HolderState.NotHeld, null)
                : new HolderQuery(HolderState.Held, record.Pid > 0 ? record.Pid : null);
        }
        // /proc/locks prints the superblock device (the id findmnt shows). statx stx_dev
        // on overlay can be a different minor, so a needle built only from stx_dev misses
        // a lock that is actually held. That miss must not become NotHeld: TryAcquire
        // never runs, and Probe reports the file free. A pid hint in the file is not consulted.
        string? text = null;
        var locksReadable = false;
        try
        {
            text = File.ReadAllText(ProcLocksPath);
            locksReadable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            text = null;
        }

        var statOk = TryStat(path, out _, out var major, out var minor, out var ino, out var mntId);
        if (_statMajorOverride is uint forcedMajor && _statMinorOverride is uint forcedMinor)
        {
            major = forcedMajor;
            minor = forcedMinor;
        }

        var needles = new List<string>();
        var comparedMount = false;
        if (statOk)
        {
            needles.Add(FormatDevIno(major, minor, ino));
            foreach (var mount in MountDevices(mntId, path))
            {
                comparedMount = true;
                var needle = FormatDevIno(mount.Major, mount.Minor, ino);
                if (!needles.Contains(needle, StringComparer.OrdinalIgnoreCase))
                    needles.Add(needle);
            }
        }

        var incomplete = false;
        var deviceMismatch = false;
        var unverifiedPid = false;
        if (locksReadable && text is not null)
        {
            // A device:inode hit is not the owner until that pid's descriptor still
            // refers to this file. Another filesystem can reuse the inode.
            if (MatchVerifiedLock(text, path, needles, out var listed, out incomplete, out deviceMismatch, out unverifiedPid))
                return new HolderQuery(HolderState.Held, listed);
        }

        var scan = MatchFdTable(path);
        if (scan.Found)
            return new HolderQuery(HolderState.Held, scan.Pid);

        // A readable locks file that did not name this device:inode is not proof the
        // file is free. Permission-denied fdinfo, a half-parsed line, and a locks pid
        // whose descriptor is not this file are the same. Unknown lets Probe's
        // non-blocking take decide, without adopting a pid.
        if (!locksReadable || !statOk || !comparedMount || scan.Denied || incomplete || deviceMismatch || unverifiedPid)
            return new HolderQuery(HolderState.Unknown, null);
        return new HolderQuery(HolderState.NotHeld, null);
    }

    /// <summary>
    /// Pid of a descriptor that still refers to <paramref name="path"/> and holds a write flock.
    /// A deleted or replaced descriptor is not an owner. Null when none is visible.
    /// </summary>
    internal static int? DiscoverOwnerPid(string path)
    {
        var scan = MatchFdTable(path);
        return scan.Found ? scan.Pid : null;
    }

    /// <summary>
    /// True only when <paramref name="pid"/> has a write flock on a descriptor that is still
    /// <paramref name="path"/>. Stop calls this before any signal. A /proc/locks pid whose
    /// descriptor is missing, deleted, or a different file is not an owner.
    /// </summary>
    internal static bool OwnerDescriptorRefersTo(string path, int pid)
    {
        if (pid <= 1 || _hideFdInfo)
            return false;
        var scan = MatchProcessFds("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(path));
        return scan.Found && scan.Pid == pid;
    }

    private static bool MatchVerifiedLock(string text, string path, List<string> needles, out int pid, out bool incomplete, out bool deviceMismatch, out bool unverifiedPid)
    {
        pid = 0;
        incomplete = false;
        deviceMismatch = false;
        unverifiedPid = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.IndexOf("FLOCK", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!TryParseFlockWrite(raw, out var linePid, out var devIno) || devIno.Length == 0)
            {
                incomplete = true;
                continue;
            }

            var matched = false;
            foreach (var needle in needles)
            {
                if (!devIno.Equals(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                matched = true;
                break;
            }

            if (!matched)
            {
                deviceMismatch = true;
                continue;
            }

            if (OwnerDescriptorRefersTo(path, linePid))
            {
                pid = linePid;
                return true;
            }

            unverifiedPid = true;
        }

        return false;
    }

    private readonly record struct DevId(uint Major, uint Minor);

    private static string FormatDevIno(uint major, uint minor, ulong ino) =>
        $"{major:x2}:{minor:x2}:{ino}";

    /// <summary>
    /// Device id for this path from mountinfo. When statx already reported a mount id, that
    /// id's device is the only candidate. A longer lexical mount point can be a different
    /// filesystem (a symlink parent, or a path that crosses a mount) and must not supply a
    /// second device: the same inode there can belong to another owner. The path fallback
    /// is used only when statx has no mount id.
    /// </summary>
    private static List<DevId> MountDevices(ulong mntId, string path)
    {
        var found = new List<DevId>();
        string text;
        try
        {
            text = File.ReadAllText(MountInfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        var idText = mntId == 0 ? null : mntId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var full = Path.GetFullPath(path);
        DevId? byPath = null;
        var bestLen = -1;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length < 5 || !TryParseDev(parts[2], out var dev))
                continue;
            if (idText is not null)
            {
                if (parts[0] == idText)
                {
                    found.Add(dev);
                    return found;
                }

                continue;
            }

            var mountPoint = UnescapeMount(parts[4]);
            if (Covers(mountPoint, full) && mountPoint.Length > bestLen)
            {
                bestLen = mountPoint.Length;
                byPath = dev;
            }
        }

        if (idText is null && byPath is { } pathDev)
            found.Add(pathDev);
        return found;
    }

    private static bool TryParseDev(string text, out DevId dev)
    {
        dev = default;
        var parts = text.Split(':');
        if (parts.Length != 2)
            return false;
        if (!uint.TryParse(parts[0], out var major) || !uint.TryParse(parts[1], out var minor))
            return false;
        dev = new DevId(major, minor);
        return true;
    }

    private static string UnescapeMount(string mountPoint) =>
        mountPoint.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static bool Covers(string mountPoint, string fullPath)
    {
        if (mountPoint.Length == 0)
            return false;
        if (string.Equals(mountPoint, fullPath, StringComparison.Ordinal))
            return true;
        var root = mountPoint.EndsWith('/') ? mountPoint : mountPoint + "/";
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private readonly record struct FdScan(bool Found, bool Denied, int Pid);

    /// <summary>
    /// Find a write flock whose open file is still the current inode at <paramref name="path"/>.
    /// <see cref="FdScan.Denied"/> means the fd table could not be read completely.
    /// </summary>
    private static FdScan MatchFdTable(string path)
    {
        if (_hideFdInfo)
            return new FdScan(false, true, 0);

        var full = Path.GetFullPath(path);
        var self = MatchProcessFds("/proc/self", full);
        if (self.Found)
            return self;

        var denied = self.Denied;
        IEnumerable<string> procs;
        try
        {
            procs = Directory.EnumerateDirectories("/proc");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FdScan(false, true, 0);
        }

        foreach (var proc in procs)
        {
            var name = Path.GetFileName(proc);
            if (!int.TryParse(name, out var pid) || pid <= 0)
                continue;
            var scan = MatchProcessFds(proc, full);
            if (scan.Found)
                return scan;
            if (scan.Denied)
                denied = true;
        }

        return new FdScan(false, denied, 0);
    }

    private static FdScan MatchProcessFds(string procDir, string fullPath)
    {
        var fdDir = Path.Combine(procDir, "fd");
        IEnumerable<string> fds;
        try
        {
            fds = Directory.EnumerateFileSystemEntries(fdDir);
        }
        catch (UnauthorizedAccessException)
        {
            return new FdScan(false, true, 0);
        }
        catch (IOException)
        {
            return new FdScan(false, false, 0);
        }

        var denied = false;
        foreach (var fdPath in fds)
        {
            var opened = ReadProcLink(fdPath);
            if (!DescriptorIsCurrentFile(opened, fdPath, fullPath))
                continue;
            var infoPath = Path.Combine(procDir, "fdinfo", Path.GetFileName(fdPath));
            string info;
            try
            {
                info = File.ReadAllText(infoPath);
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in info.Split('\n'))
            {
                if (TryParseFlockWrite(line, out var pid, out _) && pid > 0)
                    return new FdScan(true, false, pid);
            }
        }

        return new FdScan(false, denied, 0);
    }

    /// <summary>
    /// The descriptor must still be the file at <paramref name="fullPath"/>.
    /// A deleted path, or a descriptor whose device and inode are not the current
    /// file, is a replaced lock file and is not the owner.
    /// </summary>
    private static bool DescriptorIsCurrentFile(string? opened, string fdProcPath, string fullPath)
    {
        if (opened is not null && opened.EndsWith(" (deleted)", StringComparison.Ordinal))
            return false;
        if (!TryStat(fdProcPath, out _, out var fdMajor, out var fdMinor, out var fdIno, out _, follow: true))
            return false;
        if (!TryStat(fullPath, out _, out var curMajor, out var curMinor, out var curIno, out _, follow: true))
            return false;
        return fdMajor == curMajor && fdMinor == curMinor && fdIno == curIno;
    }

    private static bool TryParseFlockWrite(string line, out int pid, out string devIno)
    {
        pid = 0;
        devIno = "";
        var trimmed = line.Trim();
        if (trimmed.StartsWith("lock:", StringComparison.Ordinal))
            trimmed = trimmed["lock:".Length..].Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return false;
        if (!parts[1].Equals("FLOCK", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!parts[3].Equals("WRITE", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!int.TryParse(parts[4], out pid) || pid <= 0)
            return false;
        if (parts.Length >= 6)
            devIno = parts[5];
        return true;
    }

    private static string? ReadProcLink(string path)
    {
        var buf = new byte[4096];
        var n = sys_readlink(path, buf, (nuint)buf.Length);
        if (n <= 0 || n >= buf.Length)
            return null;
        return Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    public static bool SignalTerm(int pid)
    {
        if (pid <= 1 || pid == Environment.ProcessId)
            return false;
        if (sys_kill(pid, SigTerm) == 0)
            return true;
        return Errno() == ESrch;
    }

    public static string ErrnoName(int err) =>
        err == EAgain ? "busy" : err == ENoEnt ? "not found" :
        err == EAcces ? "access denied" : err == ELoop ? "symlink" : "errno " + err;

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinLock
    {
        public long Start;
        public long Length;
        public int Pid;
        public short Type;
        public short Whence;
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int intel_fcntl_lock(SafeFileHandle fd, int cmd, ref DarwinLock record);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int darwin_fcntl_lock(SafeFileHandle fd, int cmd, long a, long b, long c, long d, long e, long f, ref DarwinLock record);
    private static int sys_fcntl_lock(SafeFileHandle fd, int cmd, ref DarwinLock record) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? darwin_fcntl_lock(fd, cmd, 0, 0, 0, 0, 0, 0, ref record) : intel_fcntl_lock(fd, cmd, ref record);

    private static bool TryStat(string path, out ushort mode, out uint major, out uint minor, out ulong ino, out ulong mntId, bool follow = false)
    {
        mode = 0;
        major = 0;
        minor = 0;
        ino = 0;
        mntId = 0;
        var buf = new byte[256];
        var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            var flags = follow ? 0 : AtSymlinkNoFollow;
            if (sys_statx(AtFdcwd, path, flags, StatxBasic | StatxMntId, pinned.AddrOfPinnedObject()) != 0)
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
        var mask = BitConverter.ToUInt32(buf, 0);
        if ((mask & StatxMntId) != 0)
            mntId = BitConverter.ToUInt64(buf, 0x90);
        return true;
    }

    private static int Errno() => Marshal.GetLastWin32Error();

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int linux_open(string path, int flags, int mode);

    // Darwin arm64 places variadic arguments on the stack after the eight argument registers.
    // Explicit padding gives open/fcntl the ABI they expect without a native helper library.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int darwin_open(string path, int flags, long a, long b, long c, long d, long e, long f, int mode);
    private static int sys_open(string path, int flags, int mode) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? darwin_open(path, flags, 0, 0, 0, 0, 0, 0, mode) : linux_open(path, flags, mode);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int sys_flock(SafeFileHandle fd, int operation);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int sys_fcntl_get(SafeFileHandle fd, int cmd);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int linux_fcntl_set(SafeFileHandle fd, int cmd, int arg);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int darwin_fcntl_set(SafeFileHandle fd, int cmd, long a, long b, long c, long d, long e, long f, int arg);
    private static int sys_fcntl_set(SafeFileHandle fd, int cmd, int arg) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? darwin_fcntl_set(fd, cmd, 0, 0, 0, 0, 0, 0, arg) : linux_fcntl_set(fd, cmd, arg);

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

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern long sys_readlink(string pathname, byte[] buf, nuint bufsiz);
}
