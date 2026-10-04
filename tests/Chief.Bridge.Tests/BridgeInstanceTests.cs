using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chief.Bridge.Tests;

public class BridgeInstanceTests
{
    private const string RoomSentinel = "room-sentinel-do-not-leak";
    private const string NickSentinel = "nick-sentinel-do-not-leak";
    private const string PassSentinel = "pass-sentinel-do-not-leak";
    private const string TokenSentinel = "token-sentinel-do-not-leak";
    private const string HookSentinel = "hook-secret-sentinel-do-not-leak";
    private const string PoisonSentinel = "poison-sentinel-do-not-leak";

    [Fact]
    public void Identity_hash_ignores_secrets_and_changes_with_room_or_nick()
    {
        using var dir = new TempDir();
        var tokenUrl = "ws://user:" + PassSentinel + "@127.0.0.1:9/relay?access_token=" + TokenSentinel;
        var plain = Load(dir, "ws://127.0.0.1:9/relay", RoomSentinel, NickSentinel, PassSentinel, "Ab12Cd");
        var withToken = Load(dir, tokenUrl, RoomSentinel, NickSentinel, PassSentinel, "Ab12Cd");
        var otherToken = Load(dir, "ws://127.0.0.1:9/relay?access_token=other-" + TokenSentinel, RoomSentinel, NickSentinel, null, null);
        var otherRoom = Load(dir, "ws://127.0.0.1:9/relay", RoomSentinel + "-b", NickSentinel, null, null);
        var otherNick = Load(dir, "ws://127.0.0.1:9/relay", RoomSentinel, NickSentinel + "-b", null, null);
        var otherTrip = Load(dir, "ws://127.0.0.1:9/relay", RoomSentinel, NickSentinel, "different-" + PassSentinel, "Zz99Yy");

        var path = BridgeInstance.IdentityLockPath(plain);
        Assert.Equal(path, BridgeInstance.IdentityLockPath(withToken));
        Assert.Equal(path, BridgeInstance.IdentityLockPath(otherToken));
        Assert.Equal(path, BridgeInstance.IdentityLockPath(otherTrip));
        Assert.NotEqual(path, BridgeInstance.IdentityLockPath(otherRoom));
        Assert.NotEqual(path, BridgeInstance.IdentityLockPath(otherNick));
        Assert.StartsWith(BridgeInstance.IdentityDirectory, path);
        Assert.DoesNotContain(RoomSentinel, path);
        Assert.DoesNotContain(NickSentinel, path);
        Assert.DoesNotContain(PassSentinel, path);
        Assert.DoesNotContain(TokenSentinel, path);
        Assert.DoesNotContain("Ab12Cd", path);
    }

    [Fact]
    public void The_state_lock_is_exclusive_until_released_and_names_the_kernel_owner()
    {
        using var dir = new TempDir();
        var path = BridgeInstance.StateLockPath(dir.Path);
        var first = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
        Assert.NotNull(first);
        Assert.Null(InstanceFileLock.TryAcquire(path, InstanceLockKind.State));

        var probe = BridgeInstance.Probe(path);
        Assert.True(probe.Held);
        Assert.Equal(Environment.ProcessId, probe.Pid);
        Assert.True(first!.CloseOnExec);

        var body = ReadNoLock(path);
        Assert.Equal($"pid={Environment.ProcessId}\n", body);
        Assert.DoesNotContain(RoomSentinel, body);

        first!.Dispose();
        Assert.False(BridgeInstance.Probe(path).Held);
        using var second = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
        Assert.NotNull(second);
    }

    [Fact]
    public void A_symlink_is_not_followed()
    {
        using var dir = new TempDir();
        var target = dir.File("real");
        File.WriteAllText(target, "keep");
        var link = dir.File(BridgeInstance.StateLockName);
        File.CreateSymbolicLink(link, target);

        var ex = Assert.Throws<ConfigException>(() => InstanceFileLock.TryAcquire(link, InstanceLockKind.State));
        Assert.Contains("symlink", ex.Message);
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void Overlay_device_id_mismatch_still_names_the_locks_owner()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");
        var child = StartLockHolder(path);
        try
        {
            var devIno = FlockDevIno(child.Id, path);
            Assert.NotNull(devIno);
            var bits = devIno!.Split(':');
            var major = Convert.ToUInt32(bits[0], 16);
            var minor = Convert.ToUInt32(bits[1], 16);
            Posix.SetStatDeviceOverride(major, minor == uint.MaxValue ? minor - 1 : minor + 1);
            var probe = BridgeInstance.Probe(path);
            Assert.True(probe.Held);
            Assert.Equal(child.Id, probe.Pid);
            Assert.NotEqual(sleep.Id, probe.Pid);
            Assert.False(child.HasExited);
            Assert.False(sleep.HasExited);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
        }
        finally
        {
            Posix.SetStatDeviceOverride(null, null);
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Probe_names_the_owner_when_proc_locks_does_not()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = dir.File("missing-proc-locks");
        try
        {
            var path = BridgeInstance.StateLockPath(dir.Path);
            Directory.CreateDirectory(dir.Path);
            File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");

            var idle = BridgeInstance.Probe(path);
            Assert.False(idle.Held);
            Assert.NotEqual(sleep.Id, idle.Pid);

            using var held = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
            Assert.NotNull(held);
            var probe = BridgeInstance.Probe(path);
            Assert.True(probe.Held);
            Assert.Equal(Environment.ProcessId, probe.Pid);
            Assert.False(sleep.HasExited);
            Assert.DoesNotContain(PoisonSentinel, ReadNoLock(path));
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_deleted_and_recreated_lock_file_is_not_owned_by_the_old_descriptor()
    {
        using var dir = new TempDir();
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, "pid=1\n");
        var child = StartLockHolder(path);
        try
        {
            var owned = BridgeInstance.Probe(path);
            Assert.True(owned.Held);
            Assert.Equal(child.Id, owned.Pid);

            File.Delete(path);
            File.WriteAllText(path, $"pid={child.Id}\n{PoisonSentinel}\n");

            var query = Posix.QueryFlockHolder(path);
            Assert.NotEqual(HolderState.Held, query.State);
            Assert.NotEqual(child.Id, query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(child.Id, probe.Pid);
            Assert.False(child.HasExited);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_readable_proc_locks_device_mismatch_is_unknown_and_is_not_the_owner()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");
        var fake = dir.File("locks");
        File.WriteAllText(fake, $"1: FLOCK  ADVISORY  WRITE {sleep.Id} ff:ff:1 0 EOF\n");
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = fake;
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.Equal(HolderState.Unknown, query.State);
            Assert.Null(query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(sleep.Id, probe.Pid);
            Assert.False(sleep.HasExited);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void An_incomplete_proc_locks_line_is_unknown_and_does_not_name_its_pid()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, $"pid={sleep.Id}\n");
        var fake = dir.File("locks");
        File.WriteAllText(fake, $"1: FLOCK  ADVISORY  WRITE {sleep.Id}\n");
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = fake;
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.Equal(HolderState.Unknown, query.State);
            Assert.Null(query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(sleep.Id, probe.Pid);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Hidden_fdinfo_keeps_a_held_lock_without_borrowing_a_mismatched_pid()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        using var held = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
        Assert.NotNull(held);
        var fake = dir.File("locks");
        File.WriteAllText(fake, $"1: FLOCK  ADVISORY  WRITE {sleep.Id} ff:ff:1 0 EOF\n");
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = fake;
        Posix.SetHideFdInfo(true);
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.Equal(HolderState.Unknown, query.State);
            Assert.Null(query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.True(probe.Held);
            Assert.Null(probe.Pid);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            Posix.SetHideFdInfo(false);
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_child_process_that_holds_the_flock_is_the_owner()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");
        var child = StartLockHolder(path);
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.Equal(HolderState.Held, query.State);
            Assert.Equal(child.Id, query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.True(probe.Held);
            Assert.Equal(child.Id, probe.Pid);
            Assert.NotEqual(sleep.Id, probe.Pid);
            Assert.False(child.HasExited);
            Assert.False(sleep.HasExited);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_cross_mount_lexical_device_is_not_the_owner()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var path = BridgeInstance.StateLockPath(dir.Path);
        File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");
        try
        {
            AssertDecoyMountIsNotOwner(path, sleep, symlinkParent: null);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
        }
        finally
        {
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_symlink_parent_does_not_adopt_the_lexical_mount_device()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var real = dir.File("real");
        Directory.CreateDirectory(real);
        var link = dir.File("link");
        Directory.CreateSymbolicLink(link, real);
        var path = Path.Combine(link, BridgeInstance.StateLockName);
        File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");
        try
        {
            AssertDecoyMountIsNotOwner(path, sleep, symlinkParent: link);
            Assert.Contains(PoisonSentinel, ReadNoLock(path), StringComparison.Ordinal);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}link{Path.DirectorySeparatorChar}{BridgeInstance.StateLockName}", Path.GetFullPath(path));
        }
        finally
        {
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Stop_refuses_to_signal_when_the_owner_pid_is_missing()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var cfg = RelayConfig.Load(WriteConfig(dir, "ws://127.0.0.1:9/relay", "room-" + Guid.NewGuid().ToString("N"), "n", null), false, dir.Path, _ => null);
        var path = BridgeInstance.StateLockPath(cfg.BaseDir);
        using var held = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
        Assert.NotNull(held);
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = dir.File("empty-locks");
        File.WriteAllText(Posix.ProcLocksPath, "");
        Posix.SetHideFdInfo(true);
        try
        {
            var probe = BridgeInstance.Probe(path);
            Assert.True(probe.Held);
            Assert.Null(probe.Pid);

            var stop = BridgeInstance.Stop(cfg);
            Assert.Equal(InstanceStopKind.Blocked, stop.Kind);
            Assert.Contains("could not be verified", stop.Detail);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            Posix.SetHideFdInfo(false);
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Stop_refuses_to_signal_a_locks_pid_whose_descriptor_is_not_this_file()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var cfg = RelayConfig.Load(WriteConfig(dir, "ws://127.0.0.1:9/relay", "room-" + Guid.NewGuid().ToString("N"), "n", null), false, dir.Path, _ => null);
        var path = BridgeInstance.StateLockPath(cfg.BaseDir);
        var held = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
        Assert.NotNull(held);
        string? devIno = null;
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            if (ReadLink(fd) != Path.GetFullPath(path))
                continue;
            var info = File.ReadAllText(Path.Combine("/proc/self/fdinfo", Path.GetFileName(fd)));
            foreach (var line in info.Split('\n'))
            {
                var token = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(part => part.Split(':') is { Length: 3 } bits
                        && bits[0].Length > 0 && bits[1].Length > 0 && bits[2].Length > 0
                        && bits.All(bit => bit.All(Uri.IsHexDigit)));
                if (line.Contains("FLOCK", StringComparison.Ordinal) && token is not null)
                    devIno = token;
            }
        }

        held!.Dispose();
        Assert.NotNull(devIno);
        Assert.False(Posix.OwnerDescriptorRefersTo(path, sleep.Id));

        var fake = dir.File("locks");
        File.WriteAllText(fake, $"1: FLOCK  ADVISORY  WRITE {sleep.Id} {devIno} 0 EOF\n");
        var previous = Posix.ProcLocksPath;
        Posix.ProcLocksPath = fake;
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.NotEqual(sleep.Id, query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(sleep.Id, probe.Pid);

            var stop = BridgeInstance.Stop(cfg);
            Assert.NotEqual(InstanceStopKind.Stopped, stop.Kind);
            Assert.DoesNotContain("stopped", stop.Detail);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            Posix.ProcLocksPath = previous;
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Mac_os_ofd_owner_is_not_rejected_because_proc_is_absent()
    {
        // Simulation. Platform is selected here so Darwin does not take the host path,
        // where OwnerConfirmed is already true before any override. SIGTERM stays
        // suppressed, so this does not prove a real macOS stop or restart.
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var cfg = RelayConfig.Load(WriteConfig(dir, "ws://127.0.0.1:9/relay", "room-" + Guid.NewGuid().ToString("N"), "n", null), false, dir.Path, _ => null);
        var path = BridgeInstance.StateLockPath(cfg.BaseDir);
        File.WriteAllText(path, $"pid={sleep.Id}\n");
        BridgeInstance.TestStateProbe = new InstanceProbe(true, sleep.Id);
        try
        {
            Posix.SetOwnerPlatform(Posix.OwnerPlatform.Linux);
            Assert.False(Posix.OwnerDescriptorRefersTo(path, sleep.Id));
            Assert.False(Posix.OwnerConfirmed(path, sleep.Id));

            var blocked = BridgeInstance.Stop(cfg);
            Assert.Equal(InstanceStopKind.Blocked, blocked.Kind);
            Assert.Contains("could not be verified", blocked.Detail);
            Assert.False(sleep.HasExited);

            Posix.SetOwnerPlatform(Posix.OwnerPlatform.MacOs);
            BridgeInstance.TestSuppressSignal = true;
            Assert.True(Posix.OwnerConfirmed(path, sleep.Id));
            var allowed = BridgeInstance.Stop(cfg);
            Assert.Equal(InstanceStopKind.Stopped, allowed.Kind);
            Assert.Contains("owner " + sleep.Id + " verified", allowed.Detail);
            Assert.DoesNotContain("could not be verified", allowed.Detail);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            BridgeInstance.TestStateProbe = null;
            BridgeInstance.TestSuppressSignal = false;
            Posix.SetOwnerPlatform(Posix.OwnerPlatform.Host);
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_stale_pid_hint_is_not_the_owner()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        try
        {
            var path = BridgeInstance.StateLockPath(dir.Path);
            Directory.CreateDirectory(dir.Path);
            File.WriteAllText(path, $"pid={sleep.Id}\n{PoisonSentinel}\n");

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(sleep.Id, probe.Pid);

            using var held = InstanceFileLock.TryAcquire(path, InstanceLockKind.State);
            Assert.NotNull(held);
            Assert.Equal(Environment.ProcessId, BridgeInstance.Probe(path).Pid);
            Assert.False(sleep.HasExited);
            Assert.DoesNotContain(PoisonSentinel, ReadNoLock(path));
        }
        finally
        {
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Status_does_not_take_the_lock_or_echo_a_poisoned_lock_file()
    {
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, "ws://127.0.0.1:9/relay", RoomSentinel, NickSentinel, PassSentinel);
        var stateLock = BridgeInstance.StateLockPath(BaseOf(dir));
        File.WriteAllText(stateLock, $"pid=1\n{PoisonSentinel}\n{RoomSentinel}\n");

        var (code, stdout, stderr) = await Capture("status", "--config", cfg);
        Assert.Equal(0, code);
        Assert.Contains("instance: not running", stdout);
        Assert.DoesNotContain(PoisonSentinel, stdout + stderr);
        Assert.DoesNotContain(PassSentinel, stdout + stderr);
        Assert.False(BridgeInstance.Probe(stateLock).Held);

        using var held = InstanceFileLock.TryAcquire(stateLock, InstanceLockKind.State);
        var (again, out2, err2) = await Capture("status", "--config", cfg);
        Assert.Equal(0, again);
        Assert.Contains($"instance: running (pid {Environment.ProcessId})", out2);
        var instanceLine = out2.Split('\n').First(l => l.StartsWith("instance:", StringComparison.Ordinal));
        Assert.DoesNotContain(RoomSentinel, instanceLine);
        Assert.DoesNotContain(NickSentinel, instanceLine);
        Assert.DoesNotContain(PassSentinel, out2 + err2);
        Assert.Null(InstanceFileLock.TryAcquire(stateLock, InstanceLockKind.State));
        Assert.Equal(Environment.ProcessId, BridgeInstance.Probe(stateLock).Pid);
    }

    [Fact]
    public async Task Stop_does_not_signal_a_stale_state_json_pid()
    {
        using var dir = new TempDir();
        using var sleep = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        try
        {
            var cfg = WriteConfig(dir, "ws://127.0.0.1:9/relay", "c-" + Guid.NewGuid().ToString("N"), "n", null);
            var state = Path.Combine(BaseOf(dir), "state.json");
            File.WriteAllText(state, "{\"alive\":true,\"connected\":true,\"pid\":" + sleep.Id + "}");

            var (code, _, err) = await Capture("stop", "--config", cfg);
            Assert.Equal(1, code);
            Assert.Contains("not running", err);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            try { sleep.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Help_mentions_the_instance_guard()
    {
        var parsed = CliArgs.Parse(["stop", "--config", "c.json"]);
        Assert.Equal("stop", parsed.Command);
        Assert.Equal("restart", CliArgs.Parse(["restart"]).Command);

        var (code, text, _) = await Capture("help");
        Assert.Equal(0, code);

        Assert.Contains("stop [--config", text);
        Assert.Contains("restart [--config", text);
        Assert.Contains("Exit 4", text);
    }

    [Fact]
    public async Task Simultaneous_starts_let_one_process_open_the_socket()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var room = "room-" + Guid.NewGuid().ToString("N");
        var cfg = WriteConfig(dir, server.Url, room, "bridge-test", null);
        server.ThenOnlineSet("bridge-test");

        using var dll = Launch.Dll(cfg);
        using var apphost = Launch.Apphost(cfg);
        var exited = await WaitUntilOneExits(dll, apphost);
        var live = ReferenceEquals(exited, dll) ? apphost : dll;

        Assert.True(exited.Proc.HasExited, Dump(dll, apphost));
        Assert.Equal(BridgeInstance.ExitAlreadyRunning, exited.Proc.ExitCode);
        Assert.Contains("already running", exited.Stderr.ToString());
        Assert.DoesNotContain("connecting", exited.Stdout.ToString());
        Assert.True(await WaitConnected(BaseOf(dir)), Dump(dll, apphost));
        await Task.Delay(200);
        Assert.Equal(1, server.Handshakes);
        Assert.False(live.Proc.HasExited);
        Assert.Equal(live.Proc.Id, BridgeInstance.Probe(BridgeInstance.StateLockPath(BaseOf(dir))).Pid);
    }

    [Fact]
    public async Task A_killed_owner_releases_the_lock_for_the_next_start()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, server.Url, "room-" + Guid.NewGuid().ToString("N"), "bridge-test", null);
        server.ThenOnlineSet("bridge-test");
        var stateLock = BridgeInstance.StateLockPath(BaseOf(dir));

        var first = Launch.Dll(cfg);
        try
        {
            Assert.True(await WaitHeld(stateLock, first.Proc.Id), first.Dump());
            first.KillNow();
            Assert.True(await WaitGone(stateLock), "lock still held after SIGKILL");
        }
        finally
        {
            first.Dispose();
        }

        using var second = Launch.LegacyDll(cfg);
        Assert.True(await WaitHeld(stateLock, second.Proc.Id), second.Dump());
        Assert.True(await WaitConnected(BaseOf(dir)), second.Dump());
    }

    [Fact]
    public async Task Dll_apphost_and_wrapper_cannot_replace_a_live_owner()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, server.Url, "room-" + Guid.NewGuid().ToString("N"), "bridge-test", PassSentinel);
        server.ThenOnlineSet("bridge-test");
        var stateLock = BridgeInstance.StateLockPath(BaseOf(dir));

        using var owner = Launch.Wrapper(cfg);
        Assert.True(await WaitHeld(stateLock, owner.Proc.Id), owner.Dump());

        using var viaDll = Launch.Dll(cfg);
        using var viaApphost = Launch.Apphost(cfg);
        using var viaLegacy = Launch.LegacyApphost(cfg);
        Assert.True(await WaitExit(viaDll.Proc), viaDll.Dump());
        Assert.True(await WaitExit(viaApphost.Proc), viaApphost.Dump());
        Assert.True(await WaitExit(viaLegacy.Proc), viaLegacy.Dump());
        Assert.Equal(BridgeInstance.ExitAlreadyRunning, viaDll.Proc.ExitCode);
        Assert.Equal(BridgeInstance.ExitAlreadyRunning, viaApphost.Proc.ExitCode);
        Assert.Equal(BridgeInstance.ExitAlreadyRunning, viaLegacy.Proc.ExitCode);
        foreach (var loser in new[] { viaDll, viaApphost, viaLegacy })
        {
            var text = loser.Stdout.ToString() + loser.Stderr.ToString();
            Assert.Contains("already running", text);
            Assert.DoesNotContain("connecting", loser.Stdout.ToString());
            Assert.DoesNotContain(PassSentinel, text);
        }

        Assert.False(owner.Proc.HasExited, owner.Dump());
        Assert.Equal(owner.Proc.Id, BridgeInstance.Probe(stateLock).Pid);
    }

    [Fact]
    public async Task A_second_state_directory_with_the_same_identity_does_not_open_a_socket()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var firstDir = new TempDir();
        using var secondDir = new TempDir();
        var room = RoomSentinel + "-" + Guid.NewGuid().ToString("N");
        var nick = NickSentinel;
        var cfg1 = WriteConfig(firstDir, server.Url + "?access_token=" + TokenSentinel, room, nick, PassSentinel, "Ab12Cd");
        var cfg2 = WriteConfig(secondDir, server.Url, room, nick, PassSentinel, "Zz99Yy");
        server.ThenOnlineSet(nick);

        using var owner = Launch.Dll(cfg1);
        Assert.True(await WaitConnected(BaseOf(firstDir)), owner.Dump());
        var handshakes = server.Handshakes;

        using var second = Launch.LegacyDll(cfg2);
        Assert.True(await WaitExit(second.Proc), second.Dump());
        Assert.Equal(BridgeInstance.ExitAlreadyRunning, second.Proc.ExitCode);
        var text = second.Stdout.ToString() + second.Stderr.ToString();
        Assert.Contains("host identity", text);
        Assert.DoesNotContain("connecting", second.Stdout.ToString());
        AssertNoSentinels(text);
        AssertNoSentinels(ReadNoLock(BridgeInstance.StateLockPath(BaseOf(firstDir))));
        var identity = BridgeInstance.IdentityLockPath(RelayConfig.Load(cfg1, false, firstDir.Path, _ => null));
        AssertNoSentinels(identity);
        AssertNoSentinels(ReadNoLock(identity));
        Assert.False(File.Exists(Path.Combine(BaseOf(secondDir), "outbox.jsonl")));
        await Task.Delay(200);
        Assert.Equal(handshakes, server.Handshakes);
        Assert.False(owner.Proc.HasExited);
        Assert.False(BridgeInstance.Probe(BridgeInstance.StateLockPath(BaseOf(secondDir))).Held);

        var (stopCode, _, stopErr) = await Capture("stop", "--config", cfg2);
        Assert.Equal(1, stopCode);
        Assert.Contains("host identity", stopErr);
        AssertNoSentinels(stopErr);
        Assert.False(owner.Proc.HasExited, owner.Dump());

        using var restart = Launch.Restart(cfg2);
        Assert.True(await WaitExit(restart.Proc), restart.Dump());
        Assert.Equal(1, restart.Proc.ExitCode);
        Assert.DoesNotContain("connecting", restart.Stdout.ToString());
        AssertNoSentinels(restart.Stdout.ToString() + restart.Stderr.ToString());
        Assert.Equal(handshakes, server.Handshakes);
        Assert.Equal(owner.Proc.Id, BridgeInstance.Probe(BridgeInstance.StateLockPath(BaseOf(firstDir))).Pid);
    }

    [Fact]
    public async Task Different_rooms_can_both_run()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var aDir = new TempDir();
        using var bDir = new TempDir();
        var cfgA = WriteConfig(aDir, server.Url, "room-a-" + Guid.NewGuid().ToString("N"), "bridge-test", null);
        var cfgB = WriteConfig(bDir, server.Url, "room-b-" + Guid.NewGuid().ToString("N"), "bridge-test", null);
        server.ThenOnlineSet("bridge-test");

        using var a = Launch.Dll(cfgA);
        using var b = Launch.Apphost(cfgB);
        Assert.True(await WaitConnected(BaseOf(aDir)), a.Dump());
        Assert.True(await WaitConnected(BaseOf(bDir)), b.Dump());
        Assert.False(a.Proc.HasExited);
        Assert.False(b.Proc.HasExited);
        Assert.True(server.Handshakes >= 2);
    }

    [Fact]
    public async Task Stop_while_connected_signals_the_owner_and_status_stays_read_only()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, server.Url, "room-" + Guid.NewGuid().ToString("N"), "bridge-test", PassSentinel);
        server.ThenOnlineSet("bridge-test");

        using var owner = Launch.Dll(cfg);
        Assert.True(await WaitConnected(BaseOf(dir)), owner.Dump());
        var handshakes = server.Handshakes;

        var (statusCode, statusOut, statusErr) = await Capture("status", "--config", cfg);
        Assert.Equal(0, statusCode);
        Assert.Contains($"instance: running (pid {owner.Proc.Id})", statusOut);
        Assert.Contains("pid: " + owner.Proc.Id + " (running)", statusOut);
        Assert.DoesNotContain(PassSentinel, statusOut + statusErr);
        Assert.Equal(handshakes, server.Handshakes);
        Assert.False(owner.Proc.HasExited);

        var (code, stdout, stderr) = await Capture("stop", "--config", cfg);
        Assert.Equal(0, code);
        Assert.Contains("stopped", stdout);
        Assert.DoesNotContain(PassSentinel, stdout + stderr);
        Assert.True(await WaitExit(owner.Proc), owner.Dump());
        Assert.False(BridgeInstance.Probe(BridgeInstance.StateLockPath(BaseOf(dir))).Held);
        var stateJson = File.ReadAllText(Path.Combine(BaseOf(dir), "state.json"));
        Assert.Contains("\"alive\":false", stateJson.Replace(" ", ""));
        Assert.Equal(handshakes, server.Handshakes);

        var (after, afterOut, _) = await Capture("status", "--config", cfg);
        Assert.Equal(0, after);
        Assert.Contains("instance: not running", afterOut);
    }

    [Fact]
    public async Task Restart_replaces_the_verified_owner()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, server.Url, "room-" + Guid.NewGuid().ToString("N"), "bridge-test", null);
        server.ThenOnlineSet("bridge-test");
        var stateLock = BridgeInstance.StateLockPath(BaseOf(dir));

        using var owner = Launch.Dll(cfg);
        Assert.True(await WaitConnected(BaseOf(dir)), owner.Dump());
        var firstPid = owner.Proc.Id;

        using var restart = Launch.Restart(cfg);
        Assert.True(await WaitExit(owner.Proc), owner.Dump() + restart.Dump());
        Assert.True(await WaitHeld(stateLock, restart.Proc.Id), restart.Dump());
        Assert.True(await WaitConnected(BaseOf(dir)), restart.Dump());
        Assert.NotEqual(firstPid, restart.Proc.Id);
        Assert.Contains("stopped", restart.Stdout.ToString());
        Assert.True(server.Handshakes >= 2, $"handshakes {server.Handshakes}");
        Assert.False(restart.Proc.HasExited);
    }

    [Fact]
    public async Task Say_still_appends_while_the_owner_holds_the_lock()
    {
        Built.Ensure();
        await using var server = await LocalChat.Start();
        using var dir = new TempDir();
        var cfg = WriteConfig(dir, server.Url, "room-" + Guid.NewGuid().ToString("N"), "bridge-test", null);
        server.ThenOnlineSet("bridge-test");
        using var owner = Launch.Dll(cfg);
        Assert.True(await WaitConnected(BaseOf(dir)), owner.Dump());

        var (code, stdout, _) = await Capture("say", "--config", cfg, "hello from say");
        Assert.Equal(0, code);
        Assert.Contains("queued", stdout);
        Assert.Contains("hello from say", File.ReadAllText(Path.Combine(BaseOf(dir), "outbox.jsonl")));
        Assert.False(owner.Proc.HasExited);
        Assert.Equal(owner.Proc.Id, BridgeInstance.Probe(BridgeInstance.StateLockPath(BaseOf(dir))).Pid);
    }

    private static void AssertNoSentinels(string text)
    {
        Assert.DoesNotContain(RoomSentinel, text);
        Assert.DoesNotContain(NickSentinel, text);
        Assert.DoesNotContain(PassSentinel, text);
        Assert.DoesNotContain(TokenSentinel, text);
        Assert.DoesNotContain(HookSentinel, text);
        Assert.DoesNotContain("Ab12Cd", text);
    }

    private static string ReadNoLock(string path)
    {
        var fd = sys_open(path, OperatingSystem.IsMacOS() ? 0x01000000 | 0x100 : 0x80000 | 0x20000, 0);
        if (fd < 0)
            throw new IOException("open " + path);
        try
        {
            var buf = new byte[8192];
            var n = sys_read(fd, buf, buf.Length);
            if (n < 0)
                throw new IOException("read " + path);
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        finally
        {
            sys_close(fd);
        }
    }

    private static void AssertDecoyMountIsNotOwner(string path, Process sleep, string? symlinkParent)
    {
        var inode = Inode(path);
        var resolvedDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tableDir = symlinkParent is null ? resolvedDir : Path.GetDirectoryName(symlinkParent)!;
        var table = Path.Combine(tableDir, "mountinfo-" + Guid.NewGuid().ToString("N"));
        var lines = $"50 1 255:238 / {resolvedDir} rw - ext4 /dev/wrong rw\n";
        if (symlinkParent is not null)
            lines += $"51 1 255:237 / {symlinkParent} rw - ext4 /dev/link rw\n";
        File.WriteAllText(table, lines);

        var locks = table + ".locks";
        File.WriteAllText(locks,
            $"1: FLOCK  ADVISORY  WRITE {sleep.Id} ff:ee:{inode} 0 EOF\n" +
            $"2: FLOCK  ADVISORY  WRITE {sleep.Id} ff:ed:{inode} 0 EOF\n");

        var previousLocks = Posix.ProcLocksPath;
        var previousMounts = Posix.MountInfoPath;
        Posix.ProcLocksPath = locks;
        Posix.MountInfoPath = table;
        try
        {
            var query = Posix.QueryFlockHolder(path);
            Assert.NotEqual(HolderState.Held, query.State);
            Assert.NotEqual(sleep.Id, query.Pid);

            var probe = BridgeInstance.Probe(path);
            Assert.False(probe.Held);
            Assert.NotEqual(sleep.Id, probe.Pid);
            Assert.False(sleep.HasExited);
        }
        finally
        {
            Posix.ProcLocksPath = previousLocks;
            Posix.MountInfoPath = previousMounts;
        }
    }

    private static string? FlockDevIno(int pid, string path)
    {
        var full = Path.GetFullPath(path);
        var fdDir = "/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/fd";
        foreach (var fd in Directory.EnumerateFileSystemEntries(fdDir))
        {
            if (ReadLink(fd) != full)
                continue;
            var info = File.ReadAllText(Path.Combine("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/fdinfo", Path.GetFileName(fd)));
            foreach (var line in info.Split('\n'))
            {
                var token = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(part => part.Split(':') is { Length: 3 } bits
                        && bits[0].Length > 0 && bits[1].Length > 0 && bits[2].Length > 0
                        && bits.All(bit => bit.All(Uri.IsHexDigit)));
                if (line.Contains("FLOCK", StringComparison.Ordinal) && token is not null)
                    return token;
            }
        }

        return null;
    }

    private static ulong Inode(string path)
    {
        var psi = new ProcessStartInfo("stat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("%i");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)!;
        var text = proc.StandardOutput.ReadToEnd();
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || !ulong.TryParse(text.Trim(), out var inode))
            throw new InvalidOperationException($"stat inode failed for {path}: {err}");
        return inode;
    }

    private static Process StartLockHolder(string path)
    {
        var script = Path.Combine(Path.GetDirectoryName(path)!, "hold-lock.py");
        File.WriteAllText(script, """
            import fcntl, os, sys, time
            fd = os.open(sys.argv[1], os.O_RDWR)
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            print("ready", flush=True)
            time.sleep(120)
            """);
        var psi = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(path);
        var proc = Process.Start(psi)!;
        try
        {
            var ready = proc.StandardOutput.ReadLine();
            if (ready == "ready")
                return proc;
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"lock holder did not lock ({ready}): {err}");
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static string? ReadLink(string path)
    {
        var buf = new byte[4096];
        var n = readlink(path, buf, buf.Length);
        return n < 0 ? null : Encoding.UTF8.GetString(buf, 0, n);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern int readlink(string path, byte[] buf, int size);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int sys_open(string path, int flags, int mode);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern int sys_read(int fd, byte[] buf, int count);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int sys_close(int fd);

    private static RelayConfig Load(TempDir dir, string url, string channel, string nick, string? pass, string? trip)
    {
        var path = WriteConfig(dir, url, channel, nick, pass, trip);
        return RelayConfig.Load(path, false, dir.Path, _ => null);
    }

    private static string BaseOf(TempDir dir) => Path.Combine(dir.Path, "state");

    private static string WriteConfig(TempDir dir, string url, string channel, string nick, string? pass, string? trip = null)
    {
        var baseDir = BaseOf(dir);
        Directory.CreateDirectory(baseDir);
        var obj = new JsonObject
        {
            ["url"] = url,
            ["channel"] = channel,
            ["nick"] = nick,
            ["base"] = baseDir,
            ["receive_idle_s"] = 0,
            ["hook"] = new JsonObject
            {
                ["url_env"] = "CHATBRIDGE_TEST_HOOK_URL_" + Guid.NewGuid().ToString("N"),
                ["auth_env"] = "CHATBRIDGE_TEST_HOOK_AUTH"
            }
        };
        if (pass is not null)
            obj["pass"] = pass;
        if (trip is not null)
            obj["trip"] = trip;
        var cfg = dir.File("config.json");
        File.WriteAllText(cfg, obj.ToJsonString());
        Environment.SetEnvironmentVariable((string)obj["hook"]!["auth_env"]!, HookSentinel);
        return cfg;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Capture(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    private static async Task<bool> WaitHeld(string path, int pid, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var probe = BridgeInstance.Probe(path);
            if (probe.Held && probe.Pid == pid)
                return true;
            await Task.Delay(40);
        }

        return false;
    }

    private static async Task<bool> WaitGone(string path, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (!BridgeInstance.Probe(path).Held)
                return true;
            await Task.Delay(40);
        }

        return !BridgeInstance.Probe(path).Held;
    }

    private static async Task<bool> WaitConnected(string baseDir, int ms = 10000)
    {
        var state = Path.Combine(baseDir, "state.json");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (File.Exists(state))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(state));
                    if (doc.RootElement.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True)
                        return true;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // torn write
                }
            }

            await Task.Delay(40);
        }

        return false;
    }

    private static async Task<bool> WaitExit(Process proc, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (proc.HasExited)
                return true;
            await Task.Delay(40);
        }

        return proc.HasExited;
    }

    private static async Task<Running> WaitUntilOneExits(Running a, Running b, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (a.Proc.HasExited)
                return a;
            if (b.Proc.HasExited)
                return b;
            await Task.Delay(40);
        }

        return a.Proc.HasExited ? a : b;
    }

    private static string Dump(Running a, Running b) => a.Dump() + "\n---\n" + b.Dump();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseChiefRelay.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    private static string DotnetRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "dotnet")))
            return env;
        var user = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".dotnet");
        if (File.Exists(Path.Combine(user, "dotnet")))
            return user;
        return "/usr/share/dotnet";
    }

    private static class Built
    {
        private static bool _done;

        public static string Root => RepoRoot();
        public static string Dotnet => Path.Combine(DotnetRoot(), "dotnet");
        public static string ChatDll => Path.Combine(Root, "src/ChatBridge/bin/Debug/net8.0/ChatBridge.dll");
        public static string ChatApphost => Path.Combine(Root, "src/ChatBridge/bin/Debug/net8.0/ChatBridge");
        public static string LegacyDll => Path.Combine(Root, "src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge.dll");
        public static string LegacyApphost => Path.Combine(Root, "src/Chief.Bridge/bin/Debug/net8.0/Chief.Bridge");

        public static void Ensure()
        {
            if (_done && File.Exists(ChatApphost) && File.Exists(LegacyApphost))
                return;
            foreach (var proj in new[] { "src/ChatBridge/ChatBridge.csproj", "src/Chief.Bridge/Chief.Bridge.csproj" })
            {
                var psi = new ProcessStartInfo(Dotnet, "build \"" + Path.Combine(Root, proj) + "\" -c Debug --nologo -v q")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.Environment["DOTNET_ROOT"] = DotnetRoot();
                psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                psi.Environment["DOTNET_NOLOGO"] = "1";
                using var build = Process.Start(psi)!;
                var stdout = build.StandardOutput.ReadToEnd();
                var stderr = build.StandardError.ReadToEnd();
                build.WaitForExit();
                if (build.ExitCode != 0)
                    throw new InvalidOperationException($"build {proj} failed\n{stdout}\n{stderr}");
            }

            _done = true;
        }
    }

    private sealed class Running : IDisposable
    {
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _err = new();
        public required Process Proc { get; init; }
        public StringBuilder Stdout => _out;
        public StringBuilder Stderr => _err;

        public string Dump() =>
            $"pid {Proc.Id} exited {Proc.HasExited} code {(Proc.HasExited ? Proc.ExitCode.ToString() : "?")}\nSTDOUT:\n{_out}\nSTDERR:\n{_err}";

        public void KillNow()
        {
            try
            {
                if (!Proc.HasExited)
                    Proc.Kill(entireProcessTree: true);
                Proc.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        public void Dispose() => KillNow();

        public static Running Start(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_ROOT"] = DotnetRoot();
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            var proc = Process.Start(psi)!;
            var running = new Running { Proc = proc };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (running._out) running._out.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (running._err) running._err.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return running;
        }
    }

    private static class Launch
    {
        public static Running Dll(string cfg) => Running.Start(Built.Dotnet, Built.ChatDll, "--config", cfg);
        public static Running LegacyDll(string cfg) => Running.Start(Built.Dotnet, Built.LegacyDll, "--config", cfg);
        public static Running Apphost(string cfg) => Running.Start(Built.ChatApphost, "--config", cfg);
        public static Running LegacyApphost(string cfg) => Running.Start(Built.LegacyApphost, "--config", cfg);
        public static Running Restart(string cfg) => Running.Start(Built.Dotnet, Built.ChatDll, "restart", "--config", cfg);

        public static Running Wrapper(string cfg)
        {
            var script = Path.Combine(Path.GetDirectoryName(cfg)!, "hc-wrapper");
            File.WriteAllText(script, "#!/bin/sh\nexec " + Built.Dotnet + " " + Built.ChatDll + " \"$@\"\n");
            var chmodPsi = new ProcessStartInfo("chmod") { UseShellExecute = false };
            chmodPsi.ArgumentList.Add("+x");
            chmodPsi.ArgumentList.Add(script);
            using var chmod = Process.Start(chmodPsi)!;
            chmod.WaitForExit();
            if (chmod.ExitCode != 0)
                throw new InvalidOperationException("chmod failed for " + script);
            return Running.Start(script, "--config", cfg);
        }
    }
}
