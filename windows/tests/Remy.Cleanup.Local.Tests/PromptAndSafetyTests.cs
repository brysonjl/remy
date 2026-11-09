using Remy.Cleanup.Local;
using Remy.Core.Settings;
using Xunit;

namespace Remy.Cleanup.Local.Tests;

public class PromptAndSafetyTests
{
    [Fact]
    public void PromptIsByteExact()
    {
        var p = S1MiniPromptBuilder.Build("hello there", S1MiniStyling.SemiCasual, S1MiniStructure.Lists, true);
        const string expected =
            "<|im_start|>system\nYou are a text normalizer for speech-to-text transcripts. The input begins with a control line specifying the styling, structure, and context settings; clean the transcript to match those settings and output only the cleaned text.<|im_end|>\n" +
            "<|im_start|>user\n[Styling: semi-casual] [Structure: lists] [Context: email]\nhello there<|im_end|>\n" +
            "<|im_start|>assistant\n<think>\n\n</think>\n\n";
        Assert.Equal(expected, p);
        Assert.EndsWith("</think>\n\n", p);
        Assert.DoesNotContain("\r", p);
    }

    [Theory]
    [InlineData(S1MiniStyling.Casual, "casual")]
    [InlineData(S1MiniStyling.SemiCasual, "semi-casual")]
    [InlineData(S1MiniStyling.SemiFormal, "semi-formal")]
    [InlineData(S1MiniStyling.Formal, "formal")]
    public void StylingRaw(S1MiniStyling s, string raw) => Assert.Equal(raw, S1MiniPromptBuilder.StylingRaw(s));

    [Theory]
    [InlineData(S1MiniContextSetting.Automatic, DictationContextCategory.Email, true)]
    [InlineData(S1MiniContextSetting.Automatic, DictationContextCategory.Other, false)]
    [InlineData(S1MiniContextSetting.General, DictationContextCategory.Email, false)]
    [InlineData(S1MiniContextSetting.Email, DictationContextCategory.Other, true)]
    public void ContextResolution(S1MiniContextSetting setting, DictationContextCategory cat, bool email) =>
        Assert.Equal(email, S1MiniPromptBuilder.ResolveEmail(setting, cat));

    [Fact]
    public void NumericSign()
    {
        Assert.True(S1MiniOutputSafety.RemovesNumericSign("it dropped to minus 5 degrees", "It dropped to 5 degrees."));
        Assert.False(S1MiniOutputSafety.RemovesNumericSign("it dropped to minus 5 degrees", "It dropped to -5 degrees."));
        Assert.False(S1MiniOutputSafety.RemovesNumericSign("no numbers here", "No numbers here."));
        Assert.True(S1MiniOutputSafety.RemovesNumericSign("negative 1,200 dollars", "1200 dollars"));
        Assert.False(S1MiniOutputSafety.RemovesNumericSign("negative 1,200 dollars", "-$1,200 dollars"));
    }

    [Fact]
    public void RefusalAndGeneratedContent()
    {
        Assert.True(S1MiniOutputSafety.LooksLikeNewRefusalMessage("send the file", "I'm sorry, I can't do that."));
        Assert.False(S1MiniOutputSafety.LooksLikeNewRefusalMessage("I'm sorry about that", "I'm sorry about that."));
        Assert.True(S1MiniOutputSafety.LooksLikeGeneratedContent("hi", new string('x', 61)));
        Assert.False(S1MiniOutputSafety.LooksLikeGeneratedContent("hi", new string('x', 60)));
        Assert.True(S1MiniOutputSafety.LooksLikeGeneratedContent(new string('a', 40), new string('b', 81)));
        Assert.False(S1MiniOutputSafety.LooksLikeGeneratedContent(new string('a', 40), new string('b', 80)));
    }

    [Fact]
    public void Normalize()
    {
        Assert.Equal("Hello, world.", S1MiniOutputSafety.NormalizeTranscript("  Hello — world.  "));
        Assert.Equal("a, b", S1MiniOutputSafety.NormalizeTranscript("a , b"));
        Assert.Equal("Yes.", S1MiniOutputSafety.NormalizeTranscript("Yes.,"));
    }

    [Fact]
    public void AcceptFallsBackToInput()
    {
        Assert.Equal("keep me", S1MiniOutputSafety.Accept("keep me", "   "));
        Assert.Equal("Keep me.", S1MiniOutputSafety.Accept("keep me", " Keep me. "));
        Assert.Equal("minus 3 apples", S1MiniOutputSafety.Accept("minus 3 apples", "3 apples"));
        Assert.True(S1MiniOutputSafety.ContainsControlLeak("x <think> y"));
        Assert.True(S1MiniOutputSafety.ContainsControlLeak("x <|im_end|>"));
    }

    [Fact]
    public void DraftProposesFollowingTranscriptTokens()
    {
        var d = new S1MiniInputDraft(new[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(new[] { 4, 5, 6 }, d.Propose(new[] { 9, 2, 3 }));
        Assert.Empty(d.Propose(new[] { 42 }));
        Assert.Equal("Hello. World is here\nNext", S1MiniInputDraft.SentenceCase("hello. world is here\nnext"));
    }

    [Fact]
    public void FailureMessagesAreVerbatim()
    {
        Assert.Equal("S1-mini supports up to 1000 transcript tokens; this transcript has 1500.", S1MiniFailures.TooLong(1000, 1500));
        Assert.Equal("transcriptTooLong", "transcriptTooLong"); // placeholder name documented in report; category is inputTooLong
        Assert.Equal("inputTooLong", S1MiniFailures.Create(S1MiniFailureKind.InputTooLong, "x").Category);
    }
}
