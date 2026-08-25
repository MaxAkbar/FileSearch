using Xunit.Sdk;
using Xunit.v3;

// CSharpDB-backed tests open many short-lived databases; serialize the Core
// test assembly so coverage instrumentation cannot overlap those writers.
[assembly: Parallelization(Mode = ParallelMode.None)]
