using Xunit;

// Resource-limit tests intentionally saturate process-wide budgets.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
