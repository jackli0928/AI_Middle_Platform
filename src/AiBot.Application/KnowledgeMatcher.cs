using System.Text;
using System.Text.RegularExpressions;
using AiBot.Domain;

namespace AiBot.Application;

public static class KnowledgeMatcher
{
    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
    public static KnowledgeEntry? Find(IEnumerable<KnowledgeEntry> entries, Guid robotId, string content)
    {
        var query = Normalize(content);
        if (query.Length == 0) return null;
        return entries.Where(x => x.Enabled && (x.RobotId is null || x.RobotId == robotId))
            .Select(x => new { Entry = x, Length = (x.Question + "\n" + x.Keywords).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize)
                .Where(term => term.Length > 0 && (x.MatchMode == "exact" ? query == term : query.Contains(term, StringComparison.Ordinal))).Select(term => term.Length).DefaultIfEmpty(0).Max() })
            .Where(x => x.Length > 0).OrderBy(x => x.Entry.Priority).ThenBy(x => x.Entry.RobotId is null ? 1 : 0).ThenByDescending(x => x.Length).ThenBy(x => x.Entry.Id).Select(x => x.Entry).FirstOrDefault();
    }
}
