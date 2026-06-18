using Xunit;

// CSharpDB-backed tests open many short-lived databases; serialize the Core
// test assembly so coverage instrumentation cannot overlap those writers.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
