using Xunit;

// Each test starts a real WPF window with WebView2 (and some start Visio and Word). Run side by side they starve each other and
// requests time out, which looks like a product failure but is not one. One at a time is slower and honest.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
