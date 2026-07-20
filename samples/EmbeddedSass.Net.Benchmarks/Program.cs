using BenchmarkDotNet.Running;
using EmbeddedSass.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
