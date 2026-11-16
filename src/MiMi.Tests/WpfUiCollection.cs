using Xunit;

namespace MiMi.Tests;

// WPF caches theme/font resources process-wide; rendering on competing STA threads can deadlock.
[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;
