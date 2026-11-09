using System.Net;
using System.Text;
using Remy.Core.Cleanup;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public class ChunkerTests
{
    [Fact]
    public void BudgetMath()
    {
        Assert.Equal(8192, CleanupChunker.Budget(null));
        Assert.Equal(4096, CleanupChunker.Budget(4096));
        Assert.Equal(8192, CleanupChunker.Budget(100_000));
        Assert.Equal(128, CleanupChunker.OutputReserve(10));
        Assert.Equal(1000 * 13 / 10 + 64, CleanupChunker.OutputReserve(1000));
        Assert.Equal(3, CleanupChunker.Utf8("日"));
    }

    [Fact]
    public void FitsSingleRequestRules()
    {
        Assert.True(CleanupChunker.FitsSingleRequest("hello", "sys", "<transcript>hello</transcript>", 8192, null));
        Assert.False(CleanupChunker.FitsSingleRequest("hello", new string('s', 8193), "u", 8192, null));
        Assert.False(CleanupChunker.FitsSingleRequest(new string('a', 7000), new string('s', 100), new string('a', 7020), 8192, null));
        Assert.False(CleanupChunker.FitsSingleRequest("hello", "sys", "u", 8192, 100)); // reserve 128 > 100
    }

    [Fact]
    public void ShortTextIsOneChunkWithWhitespacePreserved()
    {
        var chunks = CleanupChunker.Chunks("  hello world \n", _ => true);
        var c = Assert.Single(chunks);
        Assert.Equal("  ", c.Prefix);
        Assert.Equal("hello world", c.Text);
        Assert.Equal(" \n", c.Suffix);
    }

    [Fact]
    public void LongTextSplitsOnSentenceBoundariesAndRoundTrips()
    {
        var sentence = "This is a sentence about nothing. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 200));
        var chunks = CleanupChunker.Chunks(text, t => CleanupChunker.Utf8(t) <= 1000);
        Assert.True(chunks.Count > 5);
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Prefix + c.Text + c.Suffix)));
        Assert.All(chunks, c => Assert.True(CleanupChunker.Utf8(c.Text) <= 1000));
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.EndsWith(".", c.Text));
    }

    [Fact]
    public void NeverSplitsGraphemeClusters()
    {
        var family = "👨‍👩‍👧‍👦"; // one grapheme, 25 UTF-8 bytes
        var text = string.Concat(Enumerable.Repeat(family + " ", 50));
        var chunks = CleanupChunker.Chunks(text, t => CleanupChunker.Utf8(t) <= 120);
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Prefix + c.Text + c.Suffix)));
        foreach (var c in chunks)
            foreach (var part in c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Assert.Equal(family, part);
    }

    [Fact]
    public void UnbreakableSegmentIsInputTooLong()
    {
        var text = new string('a', 5000);
        var ex = Assert.Throws<CleanupFailureException>(() => CleanupChunker.Chunks(text, t => t.Length <= 1000 && false || t.Length == 0));
        Assert.Equal("inputTooLong", ex.Failure.Category);
    }

    [Fact]
    public void CjkSentenceStopsAreBoundaries()
    {
        var text = string.Concat(Enumerable.Repeat("今天天气很好。", 300));
        var chunks = CleanupChunker.Chunks(text, t => CleanupChunker.Utf8(t) <= 600);
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Prefix + c.Text + c.Suffix)));
        Assert.All(chunks, c => Assert.EndsWith("。", c.Text));
    }
}

public class FailureTests
{
    [Fact]
    public void SeventeenCategoriesWithVerbatimMessages()
    {
        Assert.Equal(17, RemyCleanupFailure.AllCategories.Count);
        Assert.Equal("Cleanup did not finish before its time limit.", RemyCleanupFailure.Of("timeout").Message);
        Assert.Equal("The cleanup provider rejected the request. Check the model and cleanup settings.", RemyCleanupFailure.Of("requestRejected").Message);
        Assert.Equal("Cleanup input too long", RemyCleanupFailure.OverlayTitleFor("requestBudgetExceeded"));
        Assert.Equal("Cleanup unavailable", RemyCleanupFailure.OverlayTitleFor("modelUnavailable"));
        Assert.Equal("Cleanup did not complete. No detailed reason was recorded.", RemyCleanupFailure.MessageFor("legacy"));
        Assert.Equal("AI cleanup failed", RemyCleanupFailure.OverlayTitleFor(null));
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(401, "authentication")]
    [InlineData(403, "authentication")]
    [InlineData(408, "timeout")]
    [InlineData(504, "timeout")]
    [InlineData(413, "inputTooLong")]
    [InlineData(429, "rateLimited")]
    [InlineData(500, "serverUnavailable")]
    [InlineData(503, "serverUnavailable")]
    [InlineData(404, "requestRejected")]
    [InlineData(400, "requestRejected")]
    [InlineData(302, "invalidResponse")]
    public void HttpStatusTable(int status, string? category) =>
        Assert.Equal(category, RemyCleanupFailure.FromHttpStatus(status)?.Category);

    [Fact]
    public void ExceptionMapping()
    {
        Assert.Equal("cancelled", RemyCleanupFailure.Classify(new OperationCanceledException()).Category);
        Assert.Equal("timeout", RemyCleanupFailure.Classify(new OperationCanceledException(), deadlineReached: true).Category);
        Assert.Equal("transport", RemyCleanupFailure.Classify(new HttpRequestException("x")).Category);
        Assert.Equal("invalidResponse", RemyCleanupFailure.Classify(new System.Text.Json.JsonException()).Category);
        Assert.Equal("providerFailure", RemyCleanupFailure.Classify(new Exception()).Category);
        Assert.Equal("inputTooLong", RemyCleanupFailure.Classify(new CleanupFailureException("inputTooLong")).Category);
    }
}
