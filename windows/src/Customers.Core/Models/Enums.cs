namespace Customers.Core;

/// <summary>Тип находки: готовый заказ или организация — потенциальный клиент.</summary>
public enum LeadKind
{
    Order,
    Organization,
}

/// <summary>Релевантность по оценке системы (не факт).</summary>
public enum Relevance
{
    High,
    Medium,
    Low,
    Rejected,
}

/// <summary>Правило исключения.</summary>
public enum ExclusionKind
{
    Domain,
    Phone,
    Email,
    Name,
}

/// <summary>Итог сохранения найденного лида.</summary>
public enum SaveOutcome
{
    Added,
    Updated,
    Unchanged,
    Duplicate,
}

public static class EnumText
{
    public static string Raw(this LeadKind k) => k == LeadKind.Order ? "order" : "organization";

    public static LeadKind? ParseKind(string? s) => s switch {
        "order" => LeadKind.Order,
        "organization" => LeadKind.Organization,
        _ => null,
    };

    public static string Title(this LeadKind k) => k == LeadKind.Order ? "Заказ" : "Организация";

    public static string Raw(this Relevance r) => r switch {
        Relevance.High => "high",
        Relevance.Medium => "medium",
        Relevance.Low => "low",
        _ => "rejected",
    };

    public static Relevance? ParseRelevance(string? s) => s switch {
        "high" => Relevance.High,
        "medium" => Relevance.Medium,
        "low" => Relevance.Low,
        "rejected" => Relevance.Rejected,
        _ => null,
    };

    public static string Title(this Relevance r) => r switch {
        Relevance.High => "Высокая",
        Relevance.Medium => "Средняя",
        Relevance.Low => "Низкая",
        _ => "Не подходит",
    };

    public static int Rank(this Relevance r) => r switch {
        Relevance.High => 3,
        Relevance.Medium => 2,
        Relevance.Low => 1,
        _ => 0,
    };

    public static string Raw(this ExclusionKind k) => k switch {
        ExclusionKind.Domain => "domain",
        ExclusionKind.Phone => "phone",
        ExclusionKind.Email => "email",
        _ => "name",
    };

    public static ExclusionKind? ParseExclusion(string? s) => s switch {
        "domain" => ExclusionKind.Domain,
        "phone" => ExclusionKind.Phone,
        "email" => ExclusionKind.Email,
        "name" => ExclusionKind.Name,
        _ => null,
    };

    public static string Title(this ExclusionKind k) => k switch {
        ExclusionKind.Domain => "Домен",
        ExclusionKind.Phone => "Телефон",
        ExclusionKind.Email => "Email",
        _ => "Название",
    };
}
