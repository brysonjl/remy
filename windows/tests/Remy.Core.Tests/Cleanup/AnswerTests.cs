using System.Text;
using Remy.Core.Cleanup;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public class AnswerTests
{
    [Theory]
    [InlineData("a — b", "a, b")]
    [InlineData("a—b", "a, b")]
    [InlineData("你好 — 世界", "你好，世界")]
    [InlineData("wait ,what", "wait,what")]
    [InlineData("end.,", "end.")]
    [InlineData("really!, yes", "really! yes")]
    [InlineData("why?, ok", "why? ok")]
    [InlineData("好 。", "好。")]
    [InlineData("好。，", "好。")]
    [InlineData("en – dash -- kept", "en – dash -- kept")]
    [InlineData("  trim me \n", "trim me")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, CleanupAnswer.Normalize(input));

    [Fact]
    public void StructuredCleaned()
    {
        var r = CleanupAnswer.CleanedResponse("{\"action\":\"pasteCleanedText\",\"text\":\"Hello there.\"}", "hello there");
        Assert.Equal("Hello there.", r);
    }

    [Fact]
    public void StructuredAsIsReturnsOriginal()
    {
        Assert.Equal("keep me", CleanupAnswer.CleanedResponse("{\"action\":\"pasteTranscriptAsIs\",\"text\":null}", "keep me"));
    }

    [Fact]
    public void FenceIsStripped()
    {
        var r = CleanupAnswer.CleanedResponse("```json\n{\"action\":\"pasteCleanedText\",\"text\":\"Okay.\"}\n```", "okay");
        Assert.Equal("Okay.", r);
    }

    [Fact]
    public void EnvelopeInsideProseIsFound()
    {
        var r = CleanupAnswer.CleanedResponse("Sure! {\"action\":\"pasteCleanedText\",\"text\":\"Hi {there}.\"} done", "hi there");
        Assert.Equal("Hi {there}.", r);
    }

    [Fact]
    public void InvalidActionFallsBackToBrokenEnvelopeRule()
    {
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("{\"action\":\"nope\",\"text\":\"x\"}", "orig"));
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("<think>hmm</think> Hello", "orig"));
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("[1,2]", "orig"));
    }

    [Fact]
    public void PlainTextAccepted()
    {
        Assert.Equal("Hello, world.", CleanupAnswer.CleanedResponse("Hello — world.", "hello world"));
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("   ", "orig"));
    }

    [Fact]
    public void EmptyCleanedTextReturnsOriginal()
    {
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("{\"action\":\"pasteCleanedText\",\"text\":\"  \"}", "orig"));
        Assert.Equal("orig", CleanupAnswer.CleanedResponse("{\"action\":\"pasteCleanedText\"}", "orig"));
    }

    [Fact]
    public void RefusalIsOnlyNewWhenInputLacksIt()
    {
        Assert.Equal("my text", CleanupAnswer.CleanedResponse("I'm sorry, I can't help with that.", "my text"));
        Assert.Equal("I'm sorry about that.", CleanupAnswer.CleanedResponse("I'm sorry about that.", "i'm sorry about that"));
    }

    [Fact]
    public void GeneratedContentHeuristic()
    {
        Assert.False(CleanupAnswer.LooksLikeGeneratedContent("short", new string('a', 60)));
        Assert.True(CleanupAnswer.LooksLikeGeneratedContent("short", new string('a', 61)));
        var input = new string('b', 40);
        Assert.False(CleanupAnswer.LooksLikeGeneratedContent(input, new string('a', 80)));
        Assert.True(CleanupAnswer.LooksLikeGeneratedContent(input, new string('a', 81)));
        Assert.Equal("tiny", CleanupAnswer.CleanedResponse(new string('x', 200), "tiny"));
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void OllamaBodyParsing()
    {
        Assert.Equal("hi", CleanupAnswer.ParseOllama(B("{\"done\":true,\"done_reason\":\"stop\",\"response\":\" hi \"}")));
        Assert.Equal("incompleteResponse", Cat(() => CleanupAnswer.ParseOllama(B("{\"done\":false,\"response\":\"x\"}"))));
        Assert.Equal("incompleteResponse", Cat(() => CleanupAnswer.ParseOllama(B("{\"done\":true,\"done_reason\":\"length\",\"response\":\"x\"}"))));
        Assert.Equal("providerFailure", Cat(() => CleanupAnswer.ParseOllama(B("{\"error\":\"boom\"}"))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseOllama(B("{\"done\":true,\"response\":\"  \"}"))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseOllama(B("{\"done\":true,\"message\":{},\"response\":\"x\"}"))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseOllama(B("not json"))));
    }

    [Fact]
    public void CompatibleBodyParsing()
    {
        string Body(string msg, string finish = "\"stop\"") => "{\"choices\":[{\"message\":" + msg + ",\"finish_reason\":" + finish + "}]}";
        Assert.Equal("ok", CleanupAnswer.ParseCompatible(B(Body("{\"role\":\"assistant\",\"content\":\"ok\"}"))));
        Assert.Equal("a\nb", CleanupAnswer.ParseCompatible(B(Body("{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"reasoning\",\"text\":\"zzz\"},{\"type\":\"text\",\"text\":\"b\"}]}"))));
        Assert.Equal("incompleteResponse", Cat(() => CleanupAnswer.ParseCompatible(B(Body("{\"content\":\"ok\"}", "\"length\"")))));
        Assert.Equal("incompleteResponse", Cat(() => CleanupAnswer.ParseCompatible(B(Body("{\"content\":\"ok\"}", "null")))));
        Assert.Equal("requestRejected", Cat(() => CleanupAnswer.ParseCompatible(B(Body("{\"content\":null,\"refusal\":\"no\"}")))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseCompatible(B(Body("{\"content\":\"x\",\"tool_calls\":[{}]}")))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseCompatible(B(Body("{\"content\":\"  \"}")))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseCompatible(B("{\"error\":{\"message\":\"x\"}}"))));
        Assert.Equal("invalidResponse", Cat(() => CleanupAnswer.ParseCompatible(B("{\"choices\":[{},{}]}"))));
    }

    [Fact]
    public void OpenRouterAcceptsMissingFinishReason()
    {
        Assert.Equal("ok", CleanupAnswer.ParseOpenRouter(B("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}")));
        Assert.Equal("incompleteResponse", Cat(() => CleanupAnswer.ParseOpenRouter(B("{\"choices\":[{\"message\":{\"content\":\"\"}}]}"))));
    }

    [Fact]
    public void AnswerOverCapIsTooLarge()
    {
        var big = new string('a', CleanupAnswer.AnswerBytesLimit + 1);
        Assert.Equal("responseTooLarge", Cat(() => CleanupAnswer.ParseOllama(B("{\"done\":true,\"response\":\"" + big + "\"}"))));
    }

    private static string Cat(Action a)
    {
        var ex = Assert.Throws<CleanupFailureException>(a);
        return ex.Failure.Category;
    }
}
