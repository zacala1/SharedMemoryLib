using BenchmarkDotNet.Running;
using SharedMemory.Benchmark;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
