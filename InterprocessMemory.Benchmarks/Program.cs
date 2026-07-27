using BenchmarkDotNet.Running;
using InterprocessMemory.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
