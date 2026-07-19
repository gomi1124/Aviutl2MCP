using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Diagnostics;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class KnownLogClassifierTests
{
    private static readonly DateTimeOffset TIMESTAMP = new(2026, 7, 19, 7, 14, 58, TimeSpan.FromHours(9));
    private static readonly string[] EXPECTED_RULE_IDS =
    [
        "psdtoolkit.cache-missing",
        "psdtoolkit.pipe-exited",
        "psdtoolkit.effect-missing",
    ];

    [TestMethod]
    public void ClassifyReturnsAllThreePsdToolKitRulesWithEvidence()
    {
        // Arrange
        LogEntry[] entries =
        [
            CreateEntry("aviutl", "can not open file. in Plugin::InputService::openFile() [C:\\ProgramData\\aviutl2\\Script\\PSDToolKit\\6470edeedc9ffe4f.ptkcache]"),
            CreateEntry("aviutl", "[Plugin::PSDToolKit.aux2] PSDToolKit pipe exited unexpectedly"),
            CreateEntry("aviutl", "not found movement. in Effect::EffectService::findMovement()"),
        ];

        // Act
        IReadOnlyList<KnownLogMatch> matches = KnownLogClassifier.Classify(entries);

        // Assert
        Assert.HasCount(3, matches);
        CollectionAssert.AreEquivalent(
            EXPECTED_RULE_IDS,
            matches.Select(match => match.RuleId).ToArray());
        Assert.IsTrue(matches.All(match => match.Evidence.Count == 1));
        Assert.IsTrue(matches.All(match => !string.IsNullOrWhiteSpace(match.Impact)));
        Assert.IsTrue(matches.All(match => !string.IsNullOrWhiteSpace(match.Recommendation)));
    }

    [TestMethod]
    public void ClassifyBoundsEvidenceAndAvoidsUnrelatedMessages()
    {
        // Arrange
        List<LogEntry> entries = Enumerable.Range(0, 5)
            .Select(index => CreateEntry("aviutl", $"can not open file [{index:x16}.ptkcache]"))
            .Append(CreateEntry("bridge", "named pipe closed after a normal MCP client disconnect"))
            .Append(CreateEntry("aviutl", "effect not found in unrelated plugin"))
            .ToList();

        // Act
        IReadOnlyList<KnownLogMatch> matches = KnownLogClassifier.Classify(entries);

        // Assert
        Assert.HasCount(1, matches);
        KnownLogMatch match = matches[0];
        Assert.AreEqual("psdtoolkit.cache-missing", match.RuleId);
        Assert.HasCount(3, match.Evidence);
    }

    [TestMethod]
    public void ClassifyRecognizesLocalizedPipeAndEffectMessages()
    {
        // Arrange
        LogEntry[] entries =
        [
            CreateEntry("aviutl", "PSDToolKit のパイプが切断されたため補助プロセスを終了しました"),
            CreateEntry("aviutl", "PSDToolKit エフェクトが見つかりません"),
        ];

        // Act
        IReadOnlyList<KnownLogMatch> matches = KnownLogClassifier.Classify(entries);

        // Assert
        Assert.HasCount(2, matches);
    }

    [TestMethod]
    public void ClassifyBoundsEvidenceLineLength()
    {
        // Arrange
        LogEntry entry = CreateEntry(
            "aviutl",
            $"can not open file [{new string('a', 4096)}.ptkcache]");

        // Act
        KnownLogMatch match = KnownLogClassifier.Classify([entry]).Single();

        // Assert
        Assert.AreEqual(1024, match.Evidence.Single().Length);
        StringAssert.EndsWith(match.Evidence.Single(), "...");
    }

    private static LogEntry CreateEntry(string source, string message) =>
        new(TIMESTAMP, "error", source, "fixture", null, message);
}
