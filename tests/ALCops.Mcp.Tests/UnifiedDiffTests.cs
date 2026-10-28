using System.Text.RegularExpressions;
using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public sealed class UnifiedDiffTests
{
    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix}{i}\n"));

    private static string[] Headers(string diff) =>
        [.. diff.Split('\n').Where(l => l.StartsWith("@@", StringComparison.Ordinal))];

    [Fact]
    public void IdenticalTexts_GiveEmptyDiff()
    {
        var (diff, truncated) = UnifiedDiff.Build("a\nb\n", "a\nb\n");

        Assert.Equal("", diff);
        Assert.False(truncated);
    }

    [Fact]
    public void OneInsert()
    {
        var (diff, truncated) = UnifiedDiff.Build("a\nb\nc\n", "a\nb\nX\nc\n");

        Assert.Equal("@@ -1,3 +1,4 @@\n a\n b\n+X\n c\n", diff);
        Assert.False(truncated);
    }

    [Fact]
    public void OneDelete()
    {
        var (diff, _) = UnifiedDiff.Build("a\nb\nc\n", "a\nc\n");

        Assert.Equal("@@ -1,3 +1,2 @@\n a\n-b\n c\n", diff);
    }

    [Fact]
    public void OneReplace()
    {
        var (diff, _) = UnifiedDiff.Build(Lines(1, 10), Lines(1, 10).Replace("line5\n", "five\n"));

        Assert.Equal("@@ -2,7 +2,7 @@\n line2\n line3\n line4\n-line5\n+five\n line6\n line7\n line8\n", diff);
    }

    [Fact]
    public void InsertIntoEmptyFile_UsesLineZeroAndOmitsCountOfOne()
    {
        var (diff, _) = UnifiedDiff.Build("", "a\n");

        Assert.Equal("@@ -0,0 +1 @@\n+a\n", diff);
    }

    [Fact]
    public void DeleteAll_StartsNewSideAtLineZero()
    {
        var (diff, _) = UnifiedDiff.Build("a\nb\n", "");

        Assert.Equal("@@ -1,2 +0,0 @@\n-a\n-b\n", diff);
    }

    [Fact]
    public void TwoChangesFarApart_GiveTwoHunks()
    {
        var oldText = Lines(1, 20);
        var newText = oldText.Replace("line2\n", "two\n").Replace("line18\n", "eighteen\n");

        var (diff, _) = UnifiedDiff.Build(oldText, newText);

        Assert.Equal(["@@ -1,5 +1,5 @@", "@@ -15,6 +15,6 @@"], Headers(diff));
        Assert.Equal(
            "@@ -1,5 +1,5 @@\n line1\n-line2\n+two\n line3\n line4\n line5\n" +
            "@@ -15,6 +15,6 @@\n line15\n line16\n line17\n-line18\n+eighteen\n line19\n line20\n",
            diff);
    }

    [Fact]
    public void ChangesWithinTwiceTheContext_MergeIntoOneHunk()
    {
        // Six unchanged lines between the changes (2 x context): one hunk.
        var oldText = Lines(1, 20);
        var newText = oldText.Replace("line5\n", "five\n").Replace("line12\n", "twelve\n");

        var (diff, _) = UnifiedDiff.Build(oldText, newText);

        Assert.Equal(["@@ -2,14 +2,14 @@"], Headers(diff));
    }

    [Fact]
    public void ChangesOneLineBeyondTwiceTheContext_StaySeparate()
    {
        // Seven unchanged lines between the changes: two hunks.
        var oldText = Lines(1, 20);
        var newText = oldText.Replace("line5\n", "five\n").Replace("line13\n", "thirteen\n");

        Assert.Equal(2, Headers(UnifiedDiff.Build(oldText, newText).Diff).Length);
    }

    [Fact]
    public void CrlfInput_IsComparedAndEmittedWithoutTerminators()
    {
        var (diff, _) = UnifiedDiff.Build("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n");

        Assert.Equal("@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", diff);
        Assert.DoesNotContain('\r', diff);
    }

    [Fact]
    public void MixedTerminators_AreAllLineBreaks()
    {
        var (diff, _) = UnifiedDiff.Build("a\rb\nc\r\n", "a\rb\nC\r\n");

        Assert.Equal("@@ -1,3 +1,3 @@\n a\n b\n-c\n+C\n", diff);
    }

    [Fact]
    public void MissingTrailingNewline_IsMarked()
    {
        var (diff, _) = UnifiedDiff.Build("a\nb", "a\nc");

        Assert.Equal(
            "@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n",
            diff);
    }

    [Fact]
    public void AddingTheTrailingNewline_IsAChange()
    {
        var (diff, _) = UnifiedDiff.Build("a\nb", "a\nb\n");

        Assert.Equal("@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n", diff);
    }

    [Fact]
    public void UnterminatedLastLineAsContext_IsMarkedOnce()
    {
        var (diff, _) = UnifiedDiff.Build("a\nb", "A\nb");

        Assert.Equal("@@ -1,2 +1,2 @@\n-a\n+A\n b\n\\ No newline at end of file\n", diff);
    }

    [Fact]
    public void AboveTheEditDistanceCap_ProducesOneValidReplacementHunk()
    {
        // Every other line changes: 1,002 changed lines need 2,004 edits, more than MaxEditDistance.
        var count = UnifiedDiff.MaxEditDistance + 4;
        var oldText = string.Concat(Enumerable.Range(0, count).Select(i => i % 2 == 0 ? $"same{i}\n" : $"old{i}\n"));
        var newText = string.Concat(Enumerable.Range(0, count).Select(i => i % 2 == 0 ? $"same{i}\n" : $"new{i}\n"));
        var oldLines = oldText.Split('\n')[..^1];
        var newLines = newText.Split('\n')[..^1];

        var (diff, truncated) = UnifiedDiff.Build(oldText, newText);

        Assert.False(truncated);
        var header = Assert.Single(Headers(diff));
        // Only the common prefix ("same0") survives, as context; the rest is replaced in one block.
        var match = Regex.Match(header, @"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@$");
        Assert.True(match.Success, header);
        var body = diff.Split('\n')[1..^1];
        Assert.Equal(int.Parse(match.Groups[2].Value), body.Count(l => l[0] is ' ' or '-'));
        Assert.Equal(int.Parse(match.Groups[4].Value), body.Count(l => l[0] is ' ' or '+'));

        // One replacement: every deletion comes before every insertion.
        var lastDelete = Array.FindLastIndex(body, l => l[0] == '-');
        var firstInsert = Array.FindIndex(body, l => l[0] == '+');
        Assert.True(lastDelete < firstInsert);
        Assert.Equal(oldLines.Length, int.Parse(match.Groups[2].Value));
        Assert.Equal(newLines.Length, int.Parse(match.Groups[4].Value));
    }

    [Fact]
    public void BelowTheEditDistanceCap_KeepsSeparateHunks()
    {
        var oldText = Lines(1, 400);
        var newText = oldText.Replace("line10\n", "ten\n").Replace("line390\n", "threeninety\n");

        Assert.Equal(2, Headers(UnifiedDiff.Build(oldText, newText).Diff).Length);
    }

    [Fact]
    public void LargeDiff_IsTruncatedAtALineBoundary()
    {
        var oldText = Lines(1, 6000, "an old line with some padding to make it longer ");
        var newText = Lines(1, 6000, "a new line with some padding to make it longer ");

        var (diff, truncated) = UnifiedDiff.Build(oldText, newText);

        Assert.True(truncated);
        Assert.True(diff.Length <= UnifiedDiff.MaxDiffChars, $"{diff.Length}");
        Assert.True(diff.Length > UnifiedDiff.MaxDiffChars - 100, $"{diff.Length}");
        Assert.EndsWith("\n", diff);
        Assert.StartsWith("@@ -1,6000 +1,6000 @@\n", diff);
        Assert.All(diff.Split('\n')[1..^1], l => Assert.Contains(l[0], new[] { '-', '+' }));
    }

    [Fact]
    public void Truncation_NeverSeparatesALineFromItsNoNewlineMarker()
    {
        // One hunk: "-a" plus 2000 padded inserts and an unterminated last line. Size the last line so
        // its "\ No newline at end of file" marker starts exactly at the cap: the plain cut would keep
        // the line and drop its marker.
        const string oldText = "a";
        var pad = new string('p', 99);
        var body = string.Concat(Enumerable.Repeat(pad + "\n", 2000));

        var probe = UnifiedDiff.Render(oldText, body + "x", 3, int.MaxValue).ToString();
        var lastLine = new string('x', 1 + UnifiedDiff.MaxDiffChars - probe.LastIndexOf(UnifiedDiff.NoNewlineMarker, StringComparison.Ordinal));
        var newText = body + lastLine;

        var full = UnifiedDiff.Render(oldText, newText, 3, int.MaxValue).ToString();
        Assert.Equal(UnifiedDiff.MaxDiffChars, full.LastIndexOf(UnifiedDiff.NoNewlineMarker, StringComparison.Ordinal));

        var (diff, truncated) = UnifiedDiff.Build(oldText, newText);

        Assert.True(truncated);
        Assert.DoesNotContain(lastLine, diff);
        Assert.EndsWith("+" + pad + "\n", diff);
        Assert.True(diff.Length < UnifiedDiff.MaxDiffChars, $"{diff.Length}");
    }

    [Fact]
    public void Render_StopsBuildingHunksOncePastTheCap()
    {
        // Every 12th line changes: ~833 separate hunks of ~400 characters, well over the cap in total.
        var oldText = Lines(1, 10000, "a line padded out so that every hunk is longer ");
        var newText = string.Concat(Enumerable.Range(1, 10000).Select(i =>
            i % 12 == 6 ? $"a changed line padded out so every hunk is longer {i}\n" : $"a line padded out so that every hunk is longer {i}\n"));

        var full = UnifiedDiff.Render(oldText, newText, 3, int.MaxValue).ToString();
        var bounded = UnifiedDiff.Render(oldText, newText, 3, UnifiedDiff.MaxDiffChars).ToString();

        Assert.True(full.Length > UnifiedDiff.MaxDiffChars + 50_000, $"{full.Length}");
        Assert.True(bounded.Length > UnifiedDiff.MaxDiffChars, $"{bounded.Length}");
        Assert.True(bounded.Length < UnifiedDiff.MaxDiffChars + 1_000, $"{bounded.Length}");
        Assert.StartsWith(bounded, full, StringComparison.Ordinal);

        var (diff, truncated) = UnifiedDiff.Build(oldText, newText);
        Assert.True(truncated);
        Assert.StartsWith(diff, full, StringComparison.Ordinal);
    }
}
