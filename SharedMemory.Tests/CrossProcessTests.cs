using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

/// <summary>
/// Cross-process integration tests that validate actual IPC scenarios by spawning
/// a child process (dotnet SharedMemory.IpcHelper.dll) to perform complementary read/write
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
        "SharedMemory.IpcHelper.dll");

    [OneTimeSetUp]
    public void EnsureHelperExists()
    {
        if (!File.Exists(HelperDll))
            Assert.Ignore($"IpcHelper binary not found at '{HelperDll}'. " +
                          "Build the SharedMemory.IpcHelper project first.");
    }

    private static string GetUniqueName(string prefix) =>
        $"CP_{prefix}_{Guid.NewGuid():N}";

    /// <summary>Spawns a child IpcHelper process and waits for it to exit.</summary>
    private (int exitCode, string stdout, string stderr) SpawnHelper(
        string role, string bufferName, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = $"\"{HelperDll}\" {role} {bufferName}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

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

    // ── HighPerformanceSharedBuffer ──────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_HPBuffer_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("HPW");
        var opts = new SharedMemoryBufferOptions { Capacity = 256 };
        using var buf = new HighPerformanceSharedBuffer(name, opts);

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
        var opts = new SharedMemoryBufferOptions { Capacity = 256 };
        using var buf = new HighPerformanceSharedBuffer(name, opts);

        var (exit, stdout, stderr) = SpawnHelper("hpbuf_writer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");

        var result = new byte[64];
        buf.Read(result, 0);
        for (int i = 0; i < result.Length; i++)
            Assert.That(result[i], Is.EqualTo((byte)(i + 1)), $"index {i}");
    }

    // ── LockFreeCircularBuffer (SPSC) ────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_SPSC_ParentProduces_ChildConsumes()
    {
        string name = GetUniqueName("SPSC_PC");
        using var buf = new LockFreeCircularBuffer(name, 4096);

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
        using var buf = new LockFreeCircularBuffer(name, 4096);

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

    // ── StrictSharedMemory ───────────────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_StrictMemory_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("Strict_PW");
        var schema = new IpcTestSchema();
        using var mem = new StrictSharedMemory<IpcTestSchema>(name, schema);

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
        using var mem = new StrictSharedMemory<IpcTestSchema>(name, schema);

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

    // ── Schema ───────────────────────────────────────────────────────────────

    public struct IpcTestSchema : ISharedMemorySchema
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
