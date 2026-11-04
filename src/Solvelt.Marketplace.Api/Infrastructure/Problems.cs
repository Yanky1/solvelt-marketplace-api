namespace Solvelt.Marketplace.Api.Infrastructure;

public sealed record ProblemBody(string Code, string Message);

public static class Problems
{
    public static IResult Json(int status, string code, string message) =>
        Results.Json(new ProblemBody(code, message), statusCode: status);

    /// <summary>Other users' resources are 404, never 403.</summary>
    public static IResult NotFound(string message = "Not found.") => Json(404, "not_found", message);
    /// <summary>The resource existed publicly and is permanently gone (search engines drop a 410 faster than a 404).</summary>
    public static IResult Gone(string message = "Gone.") => Json(410, "gone", message);
    public static IResult BadRequest(string code, string message) => Json(400, code, message);
    public static IResult Unprocessable(string code, string message) => Json(422, code, message);
}
