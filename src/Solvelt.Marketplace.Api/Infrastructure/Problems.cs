namespace Solvelt.Marketplace.Api.Infrastructure;

public sealed record ProblemBody(string Code, string Message);
public sealed record ValidationBody(string Code, string Message, IReadOnlyDictionary<string, string> Errors);

public static class Problems
{
    public static IResult Json(int status, string code, string message) =>
        Results.Json(new ProblemBody(code, message), statusCode: status);

    /// <summary>Other users' resources are 404, never 403.</summary>
    public static IResult NotFound(string message = "Not found.") => Json(404, "not_found", message);
    public static IResult BadRequest(string code, string message) => Json(400, code, message);
    /// <summary><c>422 { code: "validation_failed", errors: { field: message } }</c>.</summary>
    public static IResult Validation(IReadOnlyDictionary<string, string> errors) =>
        Results.Json(new ValidationBody("validation_failed", "Some fields need attention.", errors), statusCode: 422);
    public static IResult Unprocessable(string code, string message) => Json(422, code, message);
}
