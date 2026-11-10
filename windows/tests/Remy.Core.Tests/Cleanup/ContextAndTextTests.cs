using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Contracts;
using Remy.Core.Settings;
using Remy.Core.Text;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public class ClassifierTests
{
    private static DictationContextCategory C(string proc, string? title = null, string? url = null, string? app = null) =>
        DictationContextClassifier.Classify(proc, title, url, app).Category;

    [Theory]
    [InlineData("OUTLOOK.EXE", DictationContextCategory.Email)]
    [InlineData("olk.exe", DictationContextCategory.Email)]
    [InlineData("HxOutlook.exe", DictationContextCategory.Email)]
    [InlineData("thunderbird.exe", DictationContextCategory.Email)]
    [InlineData("slack.exe", DictationContextCategory.WorkMessaging)]
    [InlineData("ms-teams.exe", DictationContextCategory.WorkMessaging)]
    [InlineData("Teams.exe", DictationContextCategory.WorkMessaging)]
    [InlineData("discord.exe", DictationContextCategory.WorkMessaging)]
    [InlineData("whatsapp.exe", DictationContextCategory.PersonalMessaging)]
    [InlineData("telegram.exe", DictationContextCategory.PersonalMessaging)]
    [InlineData("signal.exe", DictationContextCategory.PersonalMessaging)]
    [InlineData("WindowsTerminal.exe", DictationContextCategory.Terminal)]
    [InlineData("pwsh.exe", DictationContextCategory.Terminal)]
    [InlineData("cmd.exe", DictationContextCategory.Terminal)]
    [InlineData("mintty.exe", DictationContextCategory.Terminal)]
    [InlineData("wezterm-gui.exe", DictationContextCategory.Terminal)]
    [InlineData("cursor.exe", DictationContextCategory.CodingAgent)]
    [InlineData("windsurf.exe", DictationContextCategory.CodingAgent)]
    [InlineData("codex.exe", DictationContextCategory.CodingAgent)]
    [InlineData("claude.exe", DictationContextCategory.AiChat)]
    [InlineData("chatgpt.exe", DictationContextCategory.AiChat)]
    [InlineData("chrome.exe", DictationContextCategory.Browser)]
    [InlineData("msedge.exe", DictationContextCategory.Browser)]
    [InlineData("firefox.exe", DictationContextCategory.Browser)]
    [InlineData("brave.exe", DictationContextCategory.Browser)]
    [InlineData("arc.exe", DictationContextCategory.Browser)]
    [InlineData("code.exe", DictationContextCategory.Other)]
    [InlineData("notepad.exe", DictationContextCategory.Other)]
    [InlineData("", DictationContextCategory.Other)]
    public void ProcessTable(string proc, DictationContextCategory expected) => Assert.Equal(expected, C(proc));

    [Theory]
    [InlineData("https://mail.google.com/mail/u/0/", DictationContextCategory.Email)]
    [InlineData("outlook.office.com", DictationContextCategory.Email)]
    [InlineData("https://app.slack.com/client/T1", DictationContextCategory.WorkMessaging)]
    [InlineData("https://teams.microsoft.com/", DictationContextCategory.WorkMessaging)]
    [InlineData("https://web.whatsapp.com", DictationContextCategory.PersonalMessaging)]
    [InlineData("https://claude.ai/chat/1", DictationContextCategory.AiChat)]
    [InlineData("https://gemini.google.com/app", DictationContextCategory.AiChat)]
    [InlineData("https://www.perplexity.ai", DictationContextCategory.AiChat)]
    [InlineData("https://example.com", DictationContextCategory.Browser)]
    [InlineData("https://notgmail.com", DictationContextCategory.Browser)]
    [InlineData("https://docs.google.com", DictationContextCategory.Browser)]
    public void BrowserHostRules(string url, DictationContextCategory expected) => Assert.Equal(expected, C("chrome.exe", url: url));

    [Fact]
    public void HostRulesApplyToAnyApp()
    {
        Assert.Equal(DictationContextCategory.Email, C("notepad.exe", url: "https://mail.google.com"));
    }

    [Fact]
    public void BrowserTitleFallbackOnlyWithoutUrl()
    {
        Assert.Equal(DictationContextCategory.Email, C("msedge.exe", title: "Inbox (3) - me@x.com - Gmail"));
        Assert.Equal(DictationContextCategory.Browser, C("msedge.exe", title: "Inbox - Gmail", url: "https://example.com"));
        Assert.Equal(DictationContextCategory.AiChat, C("chrome.exe", title: "New chat - ChatGPT"));
    }

    [Theory]
    [InlineData("claude code", true)]
    [InlineData("✳ Claude Code - repo", true)]
    [InlineData("Codex CLI", true)]
    [InlineData("gemini cli", true)]
    [InlineData("aider v0.5", true)]
    [InlineData("opencode", true)]
    [InlineData("claude-code-notes.txt", false)]
    [InlineData("my/codex/dir", false)]
    [InlineData("PowerShell", false)]
    public void TerminalAgentTitles(string title, bool agent)
    {
        Assert.Equal(agent ? DictationContextCategory.TerminalAgent : DictationContextCategory.Terminal, C("WindowsTerminal.exe", title));
    }

    [Fact]
    public void CodexBuildsPresentingAsChatGpt()
    {
        Assert.Equal(DictationContextCategory.AiChat, C("codex.exe", app: " chatgpt "));
        Assert.Equal(DictationContextCategory.CodingAgent, C("codex.exe", app: "Codex"));
    }

    [Fact]
    public void AppRulesWinOverEverything()
    {
        var rules = new[]
        {
            new DictationAppRule("Slack.exe", DictationContextCategory.Email, false),
            new DictationAppRule("chrome.exe", null, true),
        };
        Assert.Equal((DictationContextCategory.Email, true), DictationContextClassifier.Classify("slack.exe", null, null, null, rules));
        // exclusion without a category keeps the automatic category but disables context
        Assert.Equal((DictationContextCategory.Email, false), DictationContextClassifier.Classify("chrome.exe", null, "https://mail.google.com", null, rules));
    }

    [Theory]
    [InlineData("1Password.exe", true)]
    [InlineData("Bitwarden.exe", true)]
    [InlineData("KeePass.exe", true)]
    [InlineData("KeePassXC.exe", true)]
    [InlineData("Dashlane.exe", true)]
    [InlineData("ProtonPass.exe", true)]
    [InlineData("CredentialUIBroker.exe", true)]
    [InlineData("LastPass.exe", true)]
    [InlineData("slack.exe", false)]
    [InlineData(null, false)]
    public void SensitiveApps(string? proc, bool sensitive) => Assert.Equal(sensitive, DictationContextClassifier.IsSensitiveApplication(proc));
}

public class SnapshotBuilderTests
{
    private static ContextPolicy Policy(bool share = false, params DictationAppRule[] rules) => new(true, share,
        new Dictionary<DictationContextCategory, DictationResolvedWritingStyle> { [DictationContextCategory.Email] = DictationResolvedWritingStyle.BuiltIn(DictationWritingStyle.Excited) },
        S1MiniAppStyling.Recommended, rules);

    private static CleanupRuntime Rt(TranscriptPostProcessingMode mode, ContextPolicy p, string? url = "https://example.com/v1", string model = "m") =>
        new(mode, "", url, model, true, null, TimeSpan.FromSeconds(30), null, null, null, true, true, true,
            S1MiniStructure.Prose, S1MiniContextSetting.Automatic, p);

    private static DestinationTarget Target(string proc, bool secure = false) =>
        new(1, 2, proc, null, "title", null, true, secure, false, false);

    private static CapturedContext Cap(string before = "Hello Wor", string? after = "ld", string? sel = null) =>
        new("outlook.exe", "Re: Budget", "https://mail.google.com/x", "mail.google.com", "Search mail", null, before, after, sel);

    [Fact]
    public void DisabledOrNonContextModesReturnNull()
    {
        Assert.Null(DictationContextSnapshotBuilder.Build(Target("a.exe"), null, Rt(TranscriptPostProcessingMode.None, Policy())));
        Assert.Null(DictationContextSnapshotBuilder.Build(Target("a.exe"), null,
            Rt(TranscriptPostProcessingMode.Ollama, Policy() with { IsEnabled = false })));
    }

    [Fact]
    public void S1GetsCategoryOnly()
    {
        var s = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.S1Mini, Policy(share: true)))!;
        Assert.Equal(DictationContextCategory.Email, s.Category);
        Assert.Equal(DictationResolvedWritingStyle.BuiltIn(DictationWritingStyle.Excited), s.Style);
        Assert.Null(s.Captured);
    }

    [Fact]
    public void RemoteTextRequiresOptInUnlessLoopback()
    {
        var remote = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.OpenRouter, Policy()))!;
        Assert.Null(remote.Captured);
        Assert.Equal("outlook", remote.AppName);
        Assert.Equal(DictationCursorPlacement.MidSentence, remote.CursorPlacement);
        Assert.True(remote.ContinuesExistingSentence);

        var opted = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.OpenRouter, Policy(share: true)))!;
        Assert.Equal("Hello Wor", opted.Captured!.TextBeforeCursor);

        var local = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.Ollama, Policy(), "http://127.0.0.1:11434", "gemma"))!;
        Assert.NotNull(local.Captured);
        var cloud = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.Ollama, Policy(), "http://127.0.0.1:11434", "gpt-oss:120b-cloud"))!;
        Assert.Null(cloud.Captured);
        var lan = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), Rt(TranscriptPostProcessingMode.Ollama, Policy(), "http://192.168.0.2:11434", "gemma"))!;
        Assert.Null(lan.Captured);
    }

    [Fact]
    public void SecureAndSensitiveAndExcludedDropEverything()
    {
        var rt = Rt(TranscriptPostProcessingMode.Ollama, Policy(), "http://127.0.0.1:11434");
        var secure = DictationContextSnapshotBuilder.Build(Target("outlook.exe", secure: true), Cap(), rt)!;
        Assert.True(secure.IsSecureField);
        Assert.Null(secure.Captured); Assert.Null(secure.AppName); Assert.Empty(secure.LexicalHints);

        var sensitive = DictationContextSnapshotBuilder.Build(Target("1Password.exe"), Cap(), rt)!;
        Assert.True(sensitive.IsExcluded);
        Assert.Null(sensitive.Captured); Assert.Null(sensitive.AppName);

        var ruled = Rt(TranscriptPostProcessingMode.Ollama, Policy(false, new DictationAppRule("outlook.exe", null, true)), "http://127.0.0.1:11434");
        var excluded = DictationContextSnapshotBuilder.Build(Target("outlook.exe"), Cap(), ruled)!;
        Assert.True(excluded.IsExcluded);
        Assert.Null(excluded.Captured);

        var ctx = DictationContextSnapshotBuilder.ToPostProcessingContext(excluded, rt)!;
        Assert.Null(ctx.AppName);
    }

    [Fact]
    public void LimitsAreApplied()
    {
        var longBefore = new string('a', 1000) + "END";
        var cap = new CapturedContext("a.exe", new string('t', 500), new string('u', 500), null, null, null, longBefore, new string('z', 900), new string('s', 700));
        var s = DictationContextSnapshotBuilder.Build(Target("a.exe"), cap, Rt(TranscriptPostProcessingMode.Ollama, Policy(), "http://localhost:11434"))!;
        var c = s.Captured!;
        Assert.Equal(600, c.TextBeforeCursor!.Length);
        Assert.EndsWith("END", c.TextBeforeCursor);
        Assert.Equal(600, c.TextAfterCursor!.Length);
        Assert.Equal(600, c.SelectedText!.Length);
        Assert.Equal(300, c.WindowTitle.Length);
        Assert.Equal(300, c.BrowserUrl!.Length);
        Assert.Equal(DictationCursorPlacement.ReplacingSelection, s.CursorPlacement);
    }

    [Theory]
    [InlineData("", "", null, DictationCursorPlacement.EmptyField, false)]
    [InlineData("", "tail", null, DictationCursorPlacement.StartOfText, false)]
    [InlineData("head", "", null, DictationCursorPlacement.EndOfText, false)]
    [InlineData("Done. ", "More", null, DictationCursorPlacement.BetweenSentences, false)]
    [InlineData("Dear Al", "x", null, DictationCursorPlacement.MidSentence, true)]
    [InlineData("Done.\n", "More", null, DictationCursorPlacement.BetweenSentences, false)]
    [InlineData("a b\n", "More", null, DictationCursorPlacement.BetweenSentences, false)]
    [InlineData("a", "b", "sel", DictationCursorPlacement.ReplacingSelection, true)]
    public void CursorPlacement(string before, string after, string? sel, DictationCursorPlacement placement, bool continues)
    {
        var (p, c) = DictationContextSnapshotBuilder.DerivePlacement(before, sel, after);
        Assert.Equal(placement, p);
        Assert.Equal(continues, c);
    }

    [Fact]
    public void FieldPurpose()
    {
        Assert.Equal(DictationFieldPurpose.SearchQuery, DictationContextSnapshotBuilder.ClassifyFieldPurpose("Search mail"));
        Assert.Equal(DictationFieldPurpose.SearchQuery, DictationContextSnapshotBuilder.ClassifyFieldPurpose(null, "SearchBox1"));
        Assert.Equal(DictationFieldPurpose.SearchQuery, DictationContextSnapshotBuilder.ClassifyFieldPurpose("Filtér results"));
        Assert.Equal(DictationFieldPurpose.Unknown, DictationContextSnapshotBuilder.ClassifyFieldPurpose("Researchers", "Subject"));
    }

    [Fact]
    public void LexicalHints()
    {
        var hints = DictationContextSnapshotBuilder.LexicalHints("Project Orion with the meeting about kubernetes_cluster and iPhone15 plus abc");
        Assert.Contains("Project", hints);
        Assert.Contains("Orion", hints);
        Assert.Contains("kubernetes_cluster", hints);
        Assert.Contains("iPhone15", hints);
        Assert.DoesNotContain("with", hints);
        Assert.DoesNotContain("abc", hints);
        var many = DictationContextSnapshotBuilder.LexicalHints(string.Join(" ", Enumerable.Range(0, 100).Select(i => "Name" + i)));
        Assert.Equal(24, many.Count);
    }
}

public class TextTests
{
    [Theory]
    [InlineData("Um, hello there", ", hello there")]
    [InlineData("I uh think so", "I think so")]
    [InlineData("well, hmm. okay", "well,. okay")]
    [InlineData("an erm answer", "an answer")]
    [InlineData("umbrella and error", "umbrella and error")]
    [InlineData("UH oh", "oh")]
    [InlineData("那个 嗯 好的", "那个 好的")]
    [InlineData("嗯，好的", "，好的")]
    [InlineData("你好嗯嗯呃", "你好")]
    public void RemovesDefaultFillers(string input, string expected) => Assert.Equal(expected, FillerWordRemover.Remove(input));

    [Fact]
    public void CustomWordsAndPunctuationCollapse()
    {
        Assert.Equal("This is good.", FillerWordRemover.Remove("This is like good like .", ["like"]));
        Assert.Equal("a b", FillerWordRemover.Remove("a  basically  b", ["basically"]));
        Assert.Equal("keep", FillerWordRemover.Remove("keep", []));
        Assert.Equal("keep", FillerWordRemover.Remove("keep", ["  "]));
        Assert.Equal("a.b", FillerWordRemover.Remove("a.b", ["a.b-c"]));   // regex metacharacters are escaped
        Assert.Equal("好 吧", FillerWordRemover.Remove("好 唔 吧", ["唔"]));
        Assert.Equal("好唔吧", FillerWordRemover.Remove("好唔吧", ["唔"]));  // non-bare CJK words need boundaries
    }

    [Fact]
    public void VocabularyParsing()
    {
        Assert.Equal(["Remy", "Parakeet", "Artboard Studio", "声"],
            VocabularyInput.Terms(" Remy, Parakeet，Artboard Studio、声\n\n Remy ,,"));
        Assert.Equal(["New"], VocabularyInput.Terms("Remy, New, remy2", ["Remy", "remy2"]));
        Assert.Equal(["a", "A"], VocabularyInput.Terms("a, A, a"));
        Assert.Equal("- A\n- B", VocabularyInput.KnownTermsSection(["A", "B"]));
        Assert.Equal("1 word", VocabularyInput.CountLabel(1));
        Assert.Equal("3 words", VocabularyInput.CountLabel(3));
        Assert.Null(VocabularyInput.FooterFor(TranscriptPostProcessingMode.S1Mini));
    }
}
