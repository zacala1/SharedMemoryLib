using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>
/// Cross-process integration tests that validate actual IPC scenarios by spawning
/// a child process (dotnet InterprocessMemory.TestWorker.dll) to perform complementary read/write
/// operations on the same named shared memory region.
///
/// These tests are the only ones that exercise the true "two separate processes share
/// memory" path; all other tests operate within a single process.
/// </summary>
[TestFixture]
[Category("CrossProcess")]
public class CrossProcessTests
{
    private static readonly string HelperDll = Path.Combine(
        AppContext.BaseDirectory,
        "InterprocessMemory.TestWorker.dll");

    [OneTimeSetUp]
    public void EnsureHelperExists()
    {
        if (!File.Exists(HelperDll))
            Assert.Ignore($"Test worker binary not found at '{HelperDll}'. " +
                          "Build the InterprocessMemory.TestWorker project first.");
    }

    private static string GetUniqueName(string prefix) =>
        $"CP_{prefix}_{Guid.NewGuid():N}";

    /// <summary>Spawns a child IpcHelper process and waits for it to exit.</summary>
    private static ProcessStartInfo CreateHelperStartInfo(
        string role,
        string bufferName,
        string? extraArgument = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add(HelperDll);
        psi.ArgumentList.Add(role);
        psi.ArgumentList.Add(bufferName);
        if (extraArgument is not null)
            psi.ArgumentList.Add(extraArgument);
        return psi;
    }

    private (int exitCode, string stdout, string stderr) SpawnHelper(
        string role,
        string bufferName,
        int timeoutMs = 15000,
        string? extraArgument = null)
    {
        ProcessStartInfo psi = CreateHelperStartInfo(role, bufferName, extraArgument);

        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutMs))
        {
            proc.Kill();
            Assert.Fail($"Child process [{role}] timed out after {timeoutMs} ms");
        }

        return (proc.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    // ── MemoryRegion ──────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_HPBuffer_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("HPW");
        using var buf = MemoryRegion.CreateOrOpen(name, 256);

        var data = new byte[64];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        buf.Write(data, 0);

        var (exit, stdout, stderr) = SpawnHelper("hpbuf_reader", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("verified"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_HPBuffer_ChildWrites_ParentReads()
    {
        string name = GetUniqueName("HPR");
        using var buf = MemoryRegion.CreateOrOpen(name, 256);

        var (exit, stdout, stderr) = SpawnHelper("hpbuf_writer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");

        var result = new byte[64];
        buf.Read(result, 0);
        for (int i = 0; i < result.Length; i++)
            Assert.That(result[i], Is.EqualTo((byte)(i + 1)), $"index {i}");
    }

    // ── SingleProducerByteStream (SPSC) ────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_SPSC_ParentProduces_ChildConsumes()
    {
        string name = GetUniqueName("SPSC_PC");
        using var buf = SingleProducerByteStream.CreateOrOpen(name, 4096);

        // Parent produces all 100 messages before child starts to avoid SPSC race
        for (int i = 0; i < 100; i++)
            Assert.That(buf.WaitWrite(BitConverter.GetBytes(i), TimeSpan.FromSeconds(5)), Is.True,
                $"WaitWrite failed at {i}");

        var (exit, stdout, stderr) = SpawnHelper("spsc_consumer", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("consumed:100"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_SPSC_ChildProduces_ParentConsumes()
    {
        string name = GetUniqueName("SPSC_CP");
        using var buf = SingleProducerByteStream.CreateOrOpen(name, 4096);

        // Child produces first, then parent consumes (avoids concurrent SPSC access)
        var (exit, stdout, stderr) = SpawnHelper("spsc_producer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");
        Assert.That(stdout, Does.Contain("produced:100"));

        var dst = new byte[4];
        for (int i = 0; i < 100; i++)
        {
            int read = buf.WaitRead(dst, TimeSpan.FromSeconds(5));
            Assert.That(read, Is.EqualTo(4),    $"Short read at {i}");
            Assert.That(BitConverter.ToInt32(dst), Is.EqualTo(i), $"Value at {i}");
        }
    }

    // ── StructuredMemory ───────────────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_StrictMemory_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("Strict_PW");
        var schema = new IpcTestSchema();
        using var mem = StructuredMemory<IpcTestSchema>.CreateOrOpen(name, schema);

        using (mem.AcquireWriteLock())
        {
            mem.Write(IpcTestSchema.Counter, 42);
            mem.WriteString(IpcTestSchema.Label, "hello");
        }

        var (exit, stdout, stderr) = SpawnHelper("strict_reader", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("strict_verified"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_StrictMemory_ChildWrites_ParentReads()
    {
        string name = GetUniqueName("Strict_CR");
        var schema = new IpcTestSchema();
        using var mem = StructuredMemory<IpcTestSchema>.CreateOrOpen(name, schema);

        var (exit, stdout, stderr) = SpawnHelper("strict_writer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");

        int counter;
        string label;
        using (mem.AcquireReadLock())
        {
            counter = mem.Read<int>(IpcTestSchema.Counter);
            label   = mem.ReadString(IpcTestSchema.Label);
        }
        Assert.That(counter, Is.EqualTo(42));
        Assert.That(label,   Is.EqualTo("hello"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_TypedSpsc_ParentProduces_ChildConsumes()
    {
        string name = GetUniqueName("TypedSpsc");
        using var queue = SingleProducerQueue<int>.CreateOrOpen(name, 128);
        for (int i = 0; i < 100; i++)
            Assert.That(queue.TryEnqueue(i), Is.True);

        var (exit, stdout, stderr) = SpawnHelper("typed_consumer", name);
        Assert.That(exit, Is.EqualTo(0), stderr);
        Assert.That(stdout, Does.Contain("typed_consumed:100"));
    }

    [Test, Timeout(60000)]
    public void CrossProcess_TypedMpmc_MultipleProcesses_DeliverExactlyOnce()
    {
        string name = GetUniqueName("TypedMpmc");
        using var queue = InterprocessMemory.ConcurrentQueue<int>.CreateOrOpen(name, 8192);
        const int producerCount = 4;
        const int perProducer = 1000;
        var processes = new List<Process>();

        try
        {
            for (int producerId = 0; producerId < producerCount; producerId++)
            {
                processes.Add(Process.Start(CreateHelperStartInfo(
                    "concurrent_producer",
                    name,
                    producerId.ToString()))!);
            }

            foreach (Process process in processes)
            {
                Assert.That(process.WaitForExit(30000), Is.True, "producer process timed out");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                Assert.That(process.ExitCode, Is.EqualTo(0), $"{stdout}\n{stderr}");
            }

            var seen = new int[producerCount * perProducer];
            for (int i = 0; i < seen.Length; i++)
            {
                Assert.That(
                    queue.TryDequeue(out int value, TimeSpan.FromSeconds(10)),
                    Is.True,
                    $"dequeue timed out at {i}");
                Assert.That(value, Is.InRange(0, seen.Length - 1));
                seen[value]++;
            }

            Assert.That(seen, Is.All.EqualTo(1));
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    [Test, Timeout(30000)]
    public void CrossProcess_WriteLock_IsMutuallyExclusive()
    {
        string name = GetUniqueName("Lock");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        try
        {
            var blocked = SpawnHelper("try_write_lock", name);
            Assert.That(blocked.exitCode, Is.EqualTo(2));
            Assert.That(blocked.stdout, Does.Contain("lock_timeout"));
        }
        finally
        {
            region.ReleaseWriteLock();
        }

        var acquired = SpawnHelper("try_write_lock", name);
        Assert.That(acquired.exitCode, Is.EqualTo(0), acquired.stderr);
        Assert.That(acquired.stdout, Does.Contain("lock_acquired"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_OrphanWriteLock_IsRecovered()
    {
        string name = GetUniqueName("Orphan");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        var orphan = SpawnHelper("orphan_write_lock", name);
        Assert.That(orphan.exitCode, Is.EqualTo(0), orphan.stderr);
        Assert.That(orphan.stdout, Does.Contain("orphan_locked"));

        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)), Is.True);
        region.ReleaseWriteLock();
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    public struct IpcTestSchema : IMemorySchema
    {
        public const string Counter = "Counter";
        public const string Label   = "Label";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Counter);
            yield return FieldDefinition.String(Label, 32);
        }
    }
}
