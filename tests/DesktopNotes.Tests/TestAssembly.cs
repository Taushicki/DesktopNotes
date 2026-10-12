using Xunit;

// Native focus and WPF dependency-property descriptor caches are shared between
// the STA threads used by these tests. Exercise windows sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
