namespace CrmAi.Infrastructure.RabbitMq;

internal static class RabbitMqEnvironmentName
{
    public static string QueueName(string? name) => ApplyPrefix(name);

    public static string ExchangeName(string? name) => ApplyPrefix(name);

    private static string ApplyPrefix(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || HasEnvironmentPrefix(normalized))
        {
            return normalized;
        }

        var separator = normalized.Contains('.') ? "." : "-";
        return $"{CurrentPrefix()}{separator}{normalized}";
    }

    private static string CurrentPrefix()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        }

        return string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)
            ? "prd"
            : "tst";
    }

    private static bool HasEnvironmentPrefix(string name) =>
        name.StartsWith("prd-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("prd.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tst-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tst.", StringComparison.OrdinalIgnoreCase);
}
