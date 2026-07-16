using BenchmarkDotNet.Running;
using EmbeddedSass.Net.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
