using AiBot.Application;
using AiBot.Domain;

namespace AiBot.Web.Models;

public sealed record MailReviewField(MailField Field, string Group, bool Required, string? ManualValue)
{
    public string? RecognizedValue => !Field.HasConflict
        ? Field.Candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Value))?.Value
        : null;
    public bool HasManualValue => !string.IsNullOrWhiteSpace(ManualValue);
    public bool NeedsInput => Field.Key != "service" && !HasManualValue && RecognizedValue is null;

    public static IReadOnlyList<MailReviewField> Create(SortedMail mail, string category)
    {
        var definitions = MailFieldCatalog.Fields.ToDictionary(field => field.Key);
        return mail.Analysis.Fields.Select(field =>
        {
            var definition = definitions.GetValueOrDefault(field.Key);
            return new MailReviewField(field, definition?.Group ?? "其他字段",
                category == "request" && definition?.RequiredForBooking == true,
                mail.Review?.Fields.GetValueOrDefault(field.Key));
        }).ToArray();
    }
}
