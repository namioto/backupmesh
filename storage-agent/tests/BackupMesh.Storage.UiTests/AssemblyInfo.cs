using Xunit;

// UI Automation drives a single shared desktop, so these tests must never run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
