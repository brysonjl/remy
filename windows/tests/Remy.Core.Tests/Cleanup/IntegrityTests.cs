using Remy.Core.Cleanup;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public class TranscriptCleanupIntegrityTests
{
    static readonly string[] Default = ["Zephyrcore"];

    [Theory]
    [InlineData("the petri dish results came back this morning", "The Zephyrcore dish results came back this morning.", "The petri dish results came back this morning.")]
    [InlineData("our core values are honesty and speed", "our core values are honesty and Zephyrcore", "our core values are honesty and speed")]
    [InlineData("the company is growing fast and we need to hire", "the company is growing fast and we need to hire Zephyrcore", "the company is growing fast and we need to hire")]
    [InlineData("we need to hire", "We need to hire Zephyrcore.", "We need to hire.")]
    [InlineData("thanks", "Zephyrcore thanks", "thanks")]
    [InlineData("zephyr core is launching next week", "Zephyrcore is launching next week.", "Zephyrcore is launching next week.")]
    [InlineData("I work at zephyrcor", "I work at Zephyrcore.", "I work at Zephyrcore.")]
    [InlineData("I work at zephyr car", "I work at Zephyrcore.", "I work at Zephyrcore.")]
    [InlineData("uh Zephyrcore ships friday", "Zephyrcore ships Friday.", "Zephyrcore ships Friday.")]
    [InlineData("reserve friday no saturday", "Reserve Saturday.", "Reserve Saturday.")]
    [InlineData("uh so the petri dish is ready um and zephyr core ships", "The Zephyrcore dish is ready, and Zephyrcore ships.", "The petri dish is ready, and Zephyrcore ships.")]
    public void Default_terms(string source, string output, string expected) =>
        Assert.Equal(expected, TranscriptCleanupIntegrity.RevertUnsupportedTerms(source, output, Default).Text);

    [Theory]
    [InlineData("open cloud code now", "Open Claude Code now.", "Open Claude Code now.", "Claude Code")]
    [InlineData("uh we use whisper", "We use Wispr.", "We use Wispr.", "Wispr")]
    [InlineData("send it to ok wanda", "Send it to Joaquín Ortega.", "Send it to ok wanda.", "Joaquín Ortega")]
    [InlineData("send it to joaquin ortega", "Send it to Joaquín Ortega.", "Send it to Joaquín Ortega.", "Joaquín Ortega")]
    public void Other_terms(string source, string output, string expected, string term) =>
        Assert.Equal(expected, TranscriptCleanupIntegrity.RevertUnsupportedTerms(source, output, [term]).Text);

    [Fact]
    public void No_terms_and_counts()
    {
        Assert.Equal(("Anything.", 0), TranscriptCleanupIntegrity.RevertUnsupportedTerms("anything", "Anything.", []));
        Assert.Equal(1, TranscriptCleanupIntegrity.RevertUnsupportedTerms("honesty and speed", "Honesty and Zephyrcore.", Default).Reverted);
    }
}
