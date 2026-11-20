using System.Text;

namespace AiBot.Application;

public static class QuoteReplySegments
{
    public static IReadOnlyList<string> Split(string reply, int maxUtf8Bytes)
    {
        if (maxUtf8Bytes < 4) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        var parts = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        var lastLineBreak = 0;
        foreach (var rune in reply.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxUtf8Bytes)
            {
                if (lastLineBreak > 0)
                {
                    parts.Add(current.ToString(0, lastLineBreak));
                    current.Remove(0, lastLineBreak);
                    bytes = Encoding.UTF8.GetByteCount(current.ToString());
                }
                else
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    bytes = 0;
                }
                lastLineBreak = 0;
            }
            current.Append(rune);
            bytes += size;
            if (rune.Value == '\n') lastLineBreak = current.Length;
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
