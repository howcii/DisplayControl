using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace DisplayControl.Server.Services;

/// <summary>
/// 校验浏览器从登录或会话接口取得的 CSRF 令牌。
/// Cookie 负责证明登录身份，独立请求头负责证明请求来自已加载本系统页面的脚本。
/// </summary>
public sealed class CsrfEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-CSRF-Token";
    public const string ClaimType = "csrf";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!RequiresValidation(context.HttpContext.Request) ||
            IsValid(context.HttpContext))
        {
            return await next(context);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "请求验证失败",
            detail: "页面安全令牌无效或已过期，请刷新页面后重试。");
    }

    public static bool RequiresValidation(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) ||
        HttpMethods.IsPut(request.Method) ||
        HttpMethods.IsPatch(request.Method) ||
        HttpMethods.IsDelete(request.Method);

    public static bool IsValid(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var expected = context.User.FindFirstValue(ClaimType);
        var providedValues = context.Request.Headers[HeaderName];
        if (string.IsNullOrEmpty(expected) || providedValues.Count != 1)
        {
            return false;
        }

        var provided = providedValues[0];
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // 固定时间比较避免令牌逐字节比较产生可观察的时间差。
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
