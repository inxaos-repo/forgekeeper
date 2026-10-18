namespace Forgekeeper.Core.Enums;

/// <summary>
/// Multi-source attribution phase 1 (#91): the <c>sources</c> table is authoritative; this catalog only
/// maps the legacy <see cref="SourceType"/> enum to well-known slugs and seeds rows during backfill.
/// </summary>
public static class SourceCatalog
{
    public sealed record WellKnown(string Slug, string Name, string Kind, string? WebsiteUrl);

    public static readonly IReadOnlyList<WellKnown> All =
    [
        new("mmf", "MyMiniFactory", "platform", "https://www.myminifactory.com"),
        new("thangs", "Thangs", "platform", "https://thangs.com"),
        new("patreon", "Patreon", "platform", "https://www.patreon.com"),
        new("cults3d", "Cults3D", "platform", "https://cults3d.com"),
        new("thingiverse", "Thingiverse", "platform", "https://www.thingiverse.com"),
        new("gumroad", "Gumroad", "platform", "https://gumroad.com"),
        new("manual", "Manual", "manual", null),
        new("unknown", "Unknown", "manual", null),
        new("telegram", "Telegram", "channel", "https://telegram.org"),
    ];

    public static string Slug(this SourceType type) => type.ToString().ToLowerInvariant();

    public static WellKnown? Find(string slug) =>
        All.FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalise a creator name/alias for duplicate detection: lower-case, alphanumerics only.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var chars = name.Normalize(System.Text.NormalizationForm.FormKD)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }
}
