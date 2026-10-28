using System.Text;
using ALCops.Mcp.Models;

namespace ALCops.Mcp.Services;

/// <summary>
/// The one place a diff is built: a line-based unified diff, hunks only (<c>@@ -a,b +c,d @@</c>
/// headers followed by <c>' '</c>/<c>'-'</c>/<c>'+'</c> lines), with no <c>---</c>/<c>+++</c> file
/// header because the file path is a sibling JSON field.
/// </summary>
/// <remarks>
/// Lines are split on <c>\r\n</c>, <c>\n</c> and <c>\r</c> and compared and emitted without their
/// terminators; every emitted line ends with <c>\n</c>, so a change of line endings alone yields no
/// hunk. Counts follow GNU diff: <c>,count</c> is omitted when it is 1, and for an empty side the
/// start is the line before the insertion point. <c>\ No newline at end of file</c> follows the last
/// line of a side that lacks a trailing newline. The common prefix and suffix are trimmed, then the
/// middle is diffed with Myers' O(ND) algorithm; above <see cref="MaxEditDistance"/> the whole middle
/// becomes one replacement hunk. Hunks separated by at most 2 × context unchanged lines merge.
/// The diff is cut at a line boundary at or before <see cref="MaxDiffChars"/> characters (200 KB,
/// 200 × 1024 characters), with <c>Truncated</c> set; hunks stop being built once the output is past
/// the cap, a line is never separated from the <c>\ No newline at end of file</c> marker that follows
/// it, the diff never ends on a hunk header without lines, and a cut hunk keeps its original header, so its counts can exceed the lines shown.
/// </remarks>
internal static class UnifiedDiff
{
    internal const int MaxDiffChars = 200 * 1024;
    internal const int MaxEditDistance = 2000;
    internal const string NoNewlineMarker = "\\ No newline at end of file";

    /// <summary>A <see cref="FileChangeReport"/> for one file's old and new text.</summary>
    internal static FileChangeReport Report(string filePath, string oldText, string newText)
    {
        var (diff, truncated) = Build(oldText, newText);
        return new FileChangeReport(filePath, diff, truncated);
    }

    /// <summary>
    /// The unified diff from <paramref name="oldText"/> to <paramref name="newText"/> with
    /// <paramref name="context"/> lines of context. Identical texts give an empty, untruncated diff.
    /// </summary>
    internal static (string Diff, bool Truncated) Build(string oldText, string newText, int context = 3)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return ("", false);

        var output = Render(oldText, newText, context, MaxDiffChars);
        if (output.Length <= MaxDiffChars)
            return (output.ToString(), false);

        return (Cut(output, MaxDiffChars), true);
    }

    /// <summary>
    /// The hunks of the diff, appended until the output is longer than <paramref name="stopAfterChars"/>:
    /// the hunk that crosses it is completed and no further hunk is built.
    /// </summary>
    internal static StringBuilder Render(string oldText, string newText, int context, int stopAfterChars)
    {
        var a = Side.Of(oldText);
        var b = Side.Of(newText);
        var ops = Normalize(Diff(a, b));

        var output = new StringBuilder();
        foreach (var (start, end) in Hunks(ops, context))
        {
            WriteHunk(output, ops, start, end, a, b);
            if (output.Length > stopAfterChars)
                break;
        }

        return output;
    }

    /// <summary>
    /// <paramref name="output"/> cut at the last line boundary at or before <paramref name="maxChars"/>;
    /// when the line after that boundary is the no-newline marker, the cut moves back one line so the
    /// marker is never separated from the line it belongs to. The result never ends on a bare hunk
    /// header: if its last line is one, the cut moves back before it too.
    /// </summary>
    internal static string Cut(StringBuilder output, int maxChars)
    {
        if (output.Length <= maxChars)
            return output.ToString();

        var cut = output.ToString(0, maxChars).LastIndexOf('\n');
        if (cut >= 0 && StartsWithAt(output, cut + 1, NoNewlineMarker))
            cut = output.ToString(0, cut).LastIndexOf('\n');

        if (cut >= 0)
        {
            var lastLineStart = output.ToString(0, cut).LastIndexOf('\n') + 1;
            if (StartsWithAt(output, lastLineStart, "@@ "))
                cut = lastLineStart - 1;
        }

        return cut < 0 ? "" : output.ToString(0, cut + 1);
    }

    private static bool StartsWithAt(StringBuilder output, int index, string value) =>
        index + value.Length <= output.Length
        && string.Equals(output.ToString(index, value.Length), value, StringComparison.Ordinal);

    private enum OpKind { Equal, Delete, Insert }

    /// <summary>One line of the edit script. <see cref="Old"/>/<see cref="New"/> are 0-based line
    /// indexes; for an insert <see cref="Old"/> (for a delete <see cref="New"/>) is the position on the
    /// other side, i.e. the number of that side's lines before it.</summary>
    private readonly record struct Op(OpKind Kind, int Old, int New);

    /// <summary>The lines of one side, and whether its last line lacks a trailing newline.</summary>
    private sealed class Side
    {
        public required string[] Lines { get; init; }
        public required bool MissingFinalNewline { get; init; }

        public int Count => Lines.Length;

        public bool IsUnterminated(int index) => MissingFinalNewline && index == Lines.Length - 1;

        public static Side Of(string text)
        {
            var lines = new List<string>();
            var start = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is '\r' or '\n')
                {
                    lines.Add(text[start..i]);
                    i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    start = i;
                }
                else
                {
                    i++;
                }
            }

            var missing = start < text.Length;
            if (missing)
                lines.Add(text[start..]);

            return new Side { Lines = [.. lines], MissingFinalNewline = missing };
        }
    }

    /// <summary>Two lines are equal when their text is, and both or neither lack the final newline.</summary>
    private static bool LinesEqual(Side a, int i, Side b, int j) =>
        a.IsUnterminated(i) == b.IsUnterminated(j)
        && string.Equals(a.Lines[i], b.Lines[j], StringComparison.Ordinal);

    private static List<Op> Diff(Side a, Side b)
    {
        int n = a.Count, m = b.Count;

        var prefix = 0;
        while (prefix < n && prefix < m && LinesEqual(a, prefix, b, prefix))
            prefix++;

        var suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && LinesEqual(a, n - 1 - suffix, b, m - 1 - suffix))
            suffix++;

        var ops = new List<Op>(n + m);
        for (var i = 0; i < prefix; i++)
            ops.Add(new Op(OpKind.Equal, i, i));

        int midN = n - prefix - suffix, midM = m - prefix - suffix;
        var middle = Myers(a, prefix, midN, b, prefix, midM);
        if (middle is null)
        {
            // Too many edits: replace the whole middle in one block.
            for (var i = 0; i < midN; i++)
                ops.Add(new Op(OpKind.Delete, prefix + i, prefix));
            for (var j = 0; j < midM; j++)
                ops.Add(new Op(OpKind.Insert, prefix + midN, prefix + j));
        }
        else
        {
            ops.AddRange(middle);
        }

        for (var s = suffix; s > 0; s--)
            ops.Add(new Op(OpKind.Equal, n - s, m - s));

        return ops;
    }

    /// <summary>
    /// Myers' greedy O(ND) shortest edit script for <c>a[aStart..aStart+n)</c> against
    /// <c>b[bStart..bStart+m)</c>, or <c>null</c> when it needs more than <see cref="MaxEditDistance"/> edits.
    /// </summary>
    private static List<Op>? Myers(Side a, int aStart, int n, Side b, int bStart, int m)
    {
        var max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>(); // trace[d] = v[-d..d] before step d

        var limit = Math.Min(max, MaxEditDistance);
        for (var d = 0; d <= limit; d++)
        {
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && LinesEqual(a, aStart + x, b, bStart + y))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                    return Backtrack(trace, n, m, aStart, bStart);
            }
        }

        return null;
    }

    private static List<Op> Backtrack(List<int[]> trace, int n, int m, int aStart, int bStart)
    {
        var reversed = new List<Op>();
        int x = n, y = m;

        for (var d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            int At(int k) => v[k + d];

            var k = x - y;
            var prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var prevX = At(prevK);
            var prevY = prevX - prevK;

            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                reversed.Add(new Op(OpKind.Equal, aStart + x, bStart + y));
            }

            reversed.Add(x == prevX
                ? new Op(OpKind.Insert, aStart + prevX, bStart + prevY)
                : new Op(OpKind.Delete, aStart + prevX, bStart + prevY));

            x = prevX;
            y = prevY;
        }

        while (x > 0 && y > 0)
        {
            x--;
            y--;
            reversed.Add(new Op(OpKind.Equal, aStart + x, bStart + y));
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>Within every run of changes, deletions come before insertions (as GNU diff prints them).</summary>
    private static List<Op> Normalize(List<Op> ops)
    {
        var result = new List<Op>(ops.Count);
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                result.Add(ops[i++]);
                continue;
            }

            var runStart = i;
            while (i < ops.Count && ops[i].Kind != OpKind.Equal)
                i++;

            var run = ops.GetRange(runStart, i - runStart);
            var firstOld = run.Min(o => o.Old);
            var firstNew = run.Min(o => o.New);
            var deletes = run.Where(o => o.Kind == OpKind.Delete).ToList();
            var inserts = run.Where(o => o.Kind == OpKind.Insert).ToList();

            result.AddRange(deletes.Select(o => o with { New = firstNew }));
            result.AddRange(inserts.Select(o => o with { Old = firstOld + deletes.Count }));
        }

        return result;
    }

    /// <summary>The op ranges [start, end] of each hunk, merging changes at most 2 × context lines apart.</summary>
    private static IEnumerable<(int Start, int End)> Hunks(List<Op> ops, int context)
    {
        var pos = 0;
        while (true)
        {
            var first = ops.FindIndex(pos, o => o.Kind != OpKind.Equal);
            if (first < 0)
                yield break;

            var last = first;
            var j = first + 1;
            while (j < ops.Count)
            {
                if (ops[j].Kind != OpKind.Equal)
                {
                    last = j++;
                    continue;
                }

                var runEnd = j;
                while (runEnd < ops.Count && ops[runEnd].Kind == OpKind.Equal)
                    runEnd++;

                if (runEnd == ops.Count || runEnd - j > 2 * context)
                    break;

                j = runEnd;
            }

            var start = Math.Max(pos, first - context);
            var end = Math.Min(ops.Count - 1, last + context);
            yield return (start, end);
            pos = end + 1;
        }
    }

    private static void WriteHunk(StringBuilder output, List<Op> ops, int start, int end, Side a, Side b)
    {
        int oldCount = 0, newCount = 0;
        for (var i = start; i <= end; i++)
        {
            if (ops[i].Kind != OpKind.Insert) oldCount++;
            if (ops[i].Kind != OpKind.Delete) newCount++;
        }

        var oldStart = oldCount == 0 ? ops[start].Old : ops[start].Old + 1;
        var newStart = newCount == 0 ? ops[start].New : ops[start].New + 1;
        output.Append("@@ -").Append(Range(oldStart, oldCount))
              .Append(" +").Append(Range(newStart, newCount)).Append(" @@\n");

        for (var i = start; i <= end; i++)
        {
            var op = ops[i];
            switch (op.Kind)
            {
                case OpKind.Equal:
                    output.Append(' ').Append(a.Lines[op.Old]).Append('\n');
                    if (a.IsUnterminated(op.Old))
                        output.Append(NoNewlineMarker).Append('\n');
                    break;
                case OpKind.Delete:
                    output.Append('-').Append(a.Lines[op.Old]).Append('\n');
                    if (a.IsUnterminated(op.Old))
                        output.Append(NoNewlineMarker).Append('\n');
                    break;
                case OpKind.Insert:
                    output.Append('+').Append(b.Lines[op.New]).Append('\n');
                    if (b.IsUnterminated(op.New))
                        output.Append(NoNewlineMarker).Append('\n');
                    break;
            }
        }
    }

    private static string Range(int start, int count) => count == 1 ? $"{start}" : $"{start},{count}";
}
