using Xunit;

// Percy exposes process-global mutable statics (the HttpClient, Enabled, sessionType,
// cliConfig, the feature-flag mirrors and the readiness-JSON seam). Several test
// collections mutate these; running collections in parallel lets one collection swap
// the shared HttpClient out from under another mid-Request, which can wedge a blocking
// .Wait()/GetResult() and intermittently deadlock the run. Serialize all collections so
// the suite is deterministic (this only affects test scheduling, not production code).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PercyIO.Playwright.Tests
{
  public class CacheTest
  {
    Cache<string, object> _cache;

    public CacheTest()
    {
      _cache = new Cache<string, object>();
    }

    [Fact]
    public void Get_ShouldGetNullValue_WhenDoesNotExists()
    {
      // Arrange
      _cache.Clear();
      string? expected = null;
      // Act
      string actual = (string)_cache.Get("abc");
      // Assert
      Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldRemoveKey_WhenExists()
    {
      // Arrange
      _cache.Clear();
      string? expected = null;
      _cache.Store("A", "abc");
      // Act
      _cache.Remove("A");
      // Assert
      var actual = (string)_cache.Get("A");
      Assert.Equal(expected, actual);
    }
  }
}
