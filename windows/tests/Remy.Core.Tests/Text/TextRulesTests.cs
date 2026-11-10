using System.Text;
using System.Text.Json.Nodes;
using Remy.Core.Settings;
using Remy.Core.Text;
using Xunit;

namespace Remy.Core.Tests.Text;

public class DictationTextRulesTests
{
    static VocabularyCorrection C(string term, params string[] spellings) => new(term, spellings);
    static DictationSnippet Sn(string trigger, string expansion) => new(Guid.NewGuid(), trigger, expansion);
    static DictationTextRules R(VocabularyCorrection[]? c = null, DictationSnippet[]? s = null) => new(c ?? [], s ?? []);

    static readonly VocabularyCorrection[] Fixture = [C("Wispr", "whisper", "wisper"), C("ZephyrCore", "zephyr core")];

    [Fact]
    public void EmptyRulesAreANoOp()
    {
        var r = R();
        Assert.True(r.IsEmpty);
        const string t = "  Keep  this exactly, please.  ";
        Assert.Equal(t, r.ApplyCorrections(t));
        Assert.Equal(t, r.ExpandSnippets(t));
        Assert.True(R([C("Wispr")]).IsEmpty);
    }

    [Theory]
    [InlineData("I use whisper daily", "I use Wispr daily")]
    [InlineData("WHISPER is great", "Wispr is great")]
    [InlineData("Zephyr Core ships", "ZephyrCore ships")]
    [InlineData("whispered whispers whisper_x xwhisper whisper2", "whispered whispers whisper_x xwhisper whisper2")]
    [InlineData("zephyr cores", "zephyr cores")]
    [InlineData("(whisper), \"whisper\"! whisper.", "(Wispr), \"Wispr\"! Wispr.")]
    [InlineData("zephyr core's roadmap", "ZephyrCore's roadmap")]
    [InlineData("zephyr core’s roadmap", "ZephyrCore’s roadmap")]
    [InlineData("whisper-based", "Wispr-based")]
    [InlineData("zephyr  core\nis", "ZephyrCore\nis")]
    [InlineData("zephyr-core", "zephyr-core")]
    public void Corrections(string input, string expected) => Assert.Equal(expected, R(Fixture).ApplyCorrections(input));

    [Fact]
    public void LongestMatchWinsAndNoCascade()
    {
        var r = R([C("Core", "core"), C("ZephyrCore", "zephyr core"), C("CoreDB", "core db")]);
        Assert.Equal("ZephyrCore", r.ApplyCorrections("zephyr core"));
        Assert.Equal("the CoreDB", r.ApplyCorrections("the core db"));
        Assert.Equal("a BCD", R([C("AB", "a b"), C("BCD", "b c d")]).ApplyCorrections("a b c d"));
        Assert.Equal("beta gamma", R([C("beta", "alpha"), C("gamma", "beta")]).ApplyCorrections("alpha beta"));
    }

    [Fact]
    public void CorrectionLeavesSnippetTriggersForLaterExpansion()
    {
        var r = R([C("Wispr", "whisper")], [Sn("whisper link", "https://example.com/w")]);
        var corrected = r.ApplyCorrections("see whisper link and whisper");
        Assert.Equal("see whisper link and Wispr", corrected);
        Assert.Equal("see https://example.com/w and Wispr", r.ExpandSnippets(corrected));
    }

    [Fact]
    public void Cjk()
    {
        Assert.Equal("我们用Wispr写。", R(Fixture).ApplyCorrections("我们用whisper写。"));
        Assert.Equal("你好，世界。", R(Fixture).ApplyCorrections("你好，世界。"));
        Assert.Equal("我在北京大学读书。", R([C("北京大学", "北经大学")]).ApplyCorrections("我在北经大学读书。"));
        Assert.Equal("これはウィスパーです", R([C("ウィスパー", "うぃすぱー")]).ApplyCorrections("これはうぃすぱーです"));
    }

    [Theory]
    [InlineData("My calendar link.")]
    [InlineData("  my calendar link  ")]
    [InlineData("\"My Calendar Link!\"")]
    public void WholeUtteranceSnippet(string input) =>
        Assert.Equal("https://cal.example/me", R(null, [Sn("my calendar link", "https://cal.example/me")]).ExpandSnippets(input));

    [Fact]
    public void SnippetInsideSentence()
    {
        var r = R(null, [Sn("my address", "1 Main St\nSpringfield")]);
        Assert.Equal("Send it to 1 Main St\nSpringfield, thanks.", r.ExpandSnippets("Send it to my address, thanks."));
        Assert.Equal("my addresses", r.ExpandSnippets("my addresses"));
    }

    [Theory]
    [InlineData("That's all over and out.", "That's all.")]
    [InlineData("over and out see you", "see you")]
    [InlineData("see over and out you", "see you")]
    [InlineData("Over and out.", "")]
    public void EmptyExpansionRemovesTheTrigger(string input, string expected) =>
        Assert.Equal(expected, R(null, [Sn("over and out", "")]).ExpandSnippets(input));

    [Fact]
    public void SnippetPriority()
    {
        var r = R([C("Sig", "signoff")], [Sn("signoff", "Best,\nAlex")]);
        Assert.Equal("ok signoff", r.ApplyCorrections("ok signoff"));
        Assert.Equal("ok Best,\nAlex", r.ExpandSnippets("ok signoff"));

        var r2 = R([C("Contacts", "address book entry")], [Sn("my email address", "alex@example.com")]);
        Assert.Equal("send my email address book entry", r2.ApplyCorrections("send my email address book entry"));
        Assert.Equal("send alex@example.com book entry", r2.ExpandSnippets("send my email address book entry"));
    }

    [Fact]
    public void ExpansionIsNotRescanned() =>
        Assert.Equal("say ping pong now", R(null, [Sn("ping", "ping pong"), Sn("pong", "ball")]).ExpandSnippets("say ping now"));

    [Fact]
    public void SnippetValidation()
    {
        var v = Sn("  hi there \n", "\n Hello!  ").Validated()!;
        Assert.Equal(("hi there", "Hello!"), (v.Trigger, v.Expansion));
        Assert.NotNull(Sn("x", "").Validated());
        Assert.Null(Sn("   ", "text").Validated());
        Assert.Null(Sn(new string('a', 61), "").Validated());
        Assert.NotNull(Sn(new string('a', 60), new string('b', 4000)).Validated());
        Assert.Null(Sn("ok", new string('b', 4001)).Validated());
    }
}

public class TextRulesSettingsTests
{
    static RemySettings Load(string json)
    {
        var s = new RemySettings();
        s.LoadFrom((JsonObject)JsonNode.Parse(json)!);
        return s;
    }

    [Fact]
    public void LegacyVocabularyLoads()
    {
        var s = Load("""{"customVocabulary":["Remy","Parakeet"]}""");
        Assert.Empty(s.VocabularyMisspellings);
        Assert.Empty(s.DictationSnippets);
        Assert.Equal(["Remy", "Parakeet"], s.CustomVocabulary);
    }

    [Fact]
    public void InvalidStoredValuesAreDropped()
    {
        Assert.Empty(Load("""{"vocabularyMisspellings":"not json"}""").VocabularyMisspellings);
        Assert.Empty(Load("""{"dictationSnippets":[{}]}""").DictationSnippets);
        var x = new string('x', 61);
        var s = Load("{\"vocabularyMisspellings\":{\"Wispr\":[\"whisper\",\"" + x + "\"],\"Empty\":[\"" + x + "\"]}}");
        Assert.Equal(["whisper"], Assert.Single(s.VocabularyMisspellings).Value);
        var t = Load($$"""{"dictationSnippets":[{"id":"6F1C3C52-6B0A-4E0B-9C1D-3A5E1B7D0001","trigger":" sig ","expansion":"Best"},{"id":"6F1C3C52-6B0A-4E0B-9C1D-3A5E1B7D0002","trigger":"{{x}}","expansion":""}]}""");
        Assert.Equal(["sig"], t.DictationSnippets.Select(d => d.Trigger));
    }

    [Fact]
    public void CorrectionAddsTermAndPersists()
    {
        var s = new RemySettings { CustomVocabulary = ["Remy"] };
        Assert.True(s.SetVocabularyCorrection(" Wispr ", ["whisper", "WHISPER", " ", "Wispr", "wisper"]));
        Assert.Equal(["Remy", "Wispr"], s.CustomVocabulary);
        var c = Assert.Single(s.VocabularyCorrections);
        Assert.Equal("Wispr", c.Term);
        Assert.Equal(["whisper", "wisper"], c.Misspellings);
        var saved = s.ToJson();
        Assert.Equal("whisper", saved["vocabularyMisspellings"]!["Wispr"]![0]!.GetValue<string>());
        Assert.False(s.SetVocabularyCorrection(new string('a', 61), ["x"]));
        // round trip
        var again = Load(saved.ToJsonString());
        Assert.Equal(["whisper", "wisper"], again.VocabularyCorrections[0].Misspellings);
    }

    [Fact]
    public void RemovingTermDropsItsSpellings()
    {
        var s = new RemySettings();
        s.SetVocabularyCorrection("Wispr", ["whisper"]);
        s.SetVocabularyCorrection("wispr", ["wisper"]);
        Assert.Equal(["Wispr"], s.CustomVocabulary);
        Assert.Equal(["wisper"], s.VocabularyMisspellings["Wispr"]);
        s.CustomVocabulary = [];
        Assert.Empty(s.VocabularyMisspellings);
    }

    [Fact]
    public void ImportMerges()
    {
        var s = new RemySettings { CustomVocabulary = ["Remy"] };
        var (imported, skipped) = s.ImportVocabulary(VocabularyCsvImport.Parse(Encoding.UTF8.GetBytes("word\nremy\nwhisper,Wispr\n")));
        Assert.Equal((1, 1), (imported, skipped));
        Assert.Equal(["Remy", "Wispr"], s.CustomVocabulary);
        Assert.Equal(["whisper"], s.VocabularyMisspellings["Wispr"]);
    }
}

public class VocabularyCsvImportTests
{
    static VocabularyCsvImport.ParseResult P(string csv) => VocabularyCsvImport.Parse(Encoding.UTF8.GetBytes(csv));
    static string[] Flat(VocabularyCsvImport.ParseResult r) => r.Entries.Select(e => $"{e.Term}|{e.Misspelling}").ToArray();

    [Fact]
    public void OneAndTwoColumns() =>
        Assert.Equal(["Remy|", "Wispr|whisper", "ZephyrCore|zephyr core"], Flat(P("Remy\nwhisper,Wispr\r\nzephyr core,ZephyrCore\n")));

    [Fact]
    public void QuotedFields() =>
        Assert.Equal(["Smith, Jones|", "Hi|say \"hi\"", "Term|two\nlines"], Flat(P("\"Smith, Jones\"\n\"say \"\"hi\"\"\",Hi\n\"two\nlines\",Term\n")));

    [Fact]
    public void Rows()
    {
        Assert.Equal([["a", "b,c", "d"], ["e"]], VocabularyCsvImport.Rows("a,\"b,c\",d\ne"));
        Assert.Equal([["", "x"]], VocabularyCsvImport.Rows("\"\",x"));
        Assert.Equal([["ab\"c\"d"]], VocabularyCsvImport.Rows("ab\"c\"d"));
    }

    [Fact]
    public void Headers()
    {
        Assert.Equal(["Wispr|whisper"], Flat(P("misspelling,correction\nwhisper,Wispr")));
        Assert.Equal(["Remy|"], Flat(P("\nWord\nRemy")));
        Assert.Equal(["Remy|", "word|"], Flat(P("Remy\nword")));
    }

    [Fact]
    public void InvalidRowsAreSkipped()
    {
        var r = P($"\n,Wispr\n{new string('x', 61)}\na,b,c\nok,\n  \nfine\n{new string('y', 60)}\n");
        Assert.Equal(["ok|", "fine|", new string('y', 60) + "|"], Flat(r));
        Assert.Equal(3, r.Skipped);
    }

    [Fact]
    public void BomIgnored() => Assert.Equal(["Remy|"], Flat(P("﻿Remy")));

    [Fact]
    public void Limits()
    {
        var r = P(string.Join("\n", Enumerable.Range(0, 1005).Select(i => "term" + i)));
        Assert.Equal((1000, 5), (r.Entries.Count, r.Skipped));
        var big = Assert.Throws<VocabularyCsvException>(() => VocabularyCsvImport.Parse(new byte[VocabularyCsvImport.MaximumBytes + 1]));
        Assert.Equal(VocabularyCsvFailure.TooLarge, big.Failure);
        var bad = Assert.Throws<VocabularyCsvException>(() => VocabularyCsvImport.Parse([0xFF, 0xFE, 0x00]));
        Assert.Equal(VocabularyCsvFailure.Unreadable, bad.Failure);
    }

    [Fact]
    public void ReadChecksSizeFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remy-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ok = Path.Combine(dir, "a.csv");
            File.WriteAllText(ok, "whisper,Wispr\n");
            Assert.Equal(["Wispr|whisper"], Flat(VocabularyCsvImport.Read(ok)));
            var big = Path.Combine(dir, "b.csv");
            File.WriteAllBytes(big, new byte[VocabularyCsvImport.MaximumBytes + 1]);
            Assert.Equal(VocabularyCsvFailure.TooLarge, Assert.Throws<VocabularyCsvException>(() => VocabularyCsvImport.Read(big)).Failure);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MergeIsCaseInsensitive()
    {
        var parsed = P("remy\nNew Term\nwhisper,wispr\nWISPER,Wispr\nwhisper,Wispr\nZephyrCore,ZephyrCore");
        var m = VocabularyCsvImport.Merge(parsed, ["Remy", "Wispr"], new Dictionary<string, IReadOnlyList<string>> { ["Wispr"] = ["whisper"] });
        Assert.Equal(["Remy", "Wispr", "New Term", "ZephyrCore"], m.Terms);
        Assert.Equal(["whisper", "WISPER"], m.Misspellings["Wispr"]);
        Assert.Equal((3, 3), (m.Imported, m.Skipped));
    }
}
