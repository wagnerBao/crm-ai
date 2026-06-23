namespace CrmAi.Application;

public sealed class OpenAiRequestException : Exception
{
    public OpenAiRequestException(string message, int? statusCode = null, string? responseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int? StatusCode { get; }

    public string? ResponseBody { get; }

    public bool IsTransient =>
        StatusCode is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504;
}
