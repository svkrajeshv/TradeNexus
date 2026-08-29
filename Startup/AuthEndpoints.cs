using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NexusApp.Services;

namespace NexusApp.Startup;

/// <summary>
/// Minimal-API endpoints that perform the actual cookie sign-in / sign-out.
/// Blazor components cannot call <c>HttpContext.SignInAsync</c> during interactive
/// rendering (the response has already started), so the login form posts here.
/// Cookies written by these endpoints are available during prerendering, which
/// eliminates the login/dashboard flip-flop caused by browser-storage auth.
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /auth/login  (form-encoded: username, password, returnUrl)
        endpoints.MapPost("/auth/login", async (HttpContext http, AuthService auth) =>
        {
            var form = await http.Request.ReadFormAsync();
            var username = (form["username"].ToString() ?? string.Empty).Trim();
            var password = form["password"].ToString() ?? string.Empty;
            var returnUrl = form["returnUrl"].ToString();

            var isValid = await auth.ValidateCredentialsAsync(username, password);
            if (!isValid)
            {
                var back = "/login?error=1";
                if (!string.IsNullOrWhiteSpace(returnUrl) && IsLocalUrl(returnUrl))
                    back += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
                return Results.Redirect(back);
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true
                    // ExpiresUtc intentionally omitted so the cookie's
                    // ExpireTimeSpan (12h) + SlidingExpiration govern the lifetime.
                });

            var target = (!string.IsNullOrWhiteSpace(returnUrl) && IsLocalUrl(returnUrl)) ? returnUrl : "/";
            return Results.Redirect(target);
        }).DisableAntiforgery();

        endpoints.MapPost("/auth/password-reset/request", async (HttpContext http, PasswordResetService resetService) =>
        {
            var form = await http.Request.ReadFormAsync();
            var phoneNumber = form["phoneNumber"].ToString();
            var sent = await resetService.RequestResetAsync(phoneNumber);

            return Results.Redirect(sent
                ? "/forgot-password?step=verify"
                : "/forgot-password?error=reset");
        }).DisableAntiforgery();

        endpoints.MapPost("/auth/password-reset/confirm", async (HttpContext http, PasswordResetService resetService) =>
        {
            var form = await http.Request.ReadFormAsync();
            var code = form["code"].ToString();
            var password = form["password"].ToString();
            var confirmPassword = form["confirmPassword"].ToString();

            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                return Results.Redirect("/forgot-password?step=verify&error=mismatch");
            }

            var reset = await resetService.ResetPasswordAsync(code, password);
            return Results.Redirect(reset
                ? "/login?reset=1"
                : "/forgot-password?step=verify&error=invalid");
        }).DisableAntiforgery();

        // POST /auth/logout
        endpoints.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login?loggedout=1");
        }).DisableAntiforgery();

        // GET /auth/logout (used by full-page navigation from the app menu)
        endpoints.MapGet("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login?loggedout=1");
        });

        return endpoints;
    }

    // Only allow same-site relative return URLs to avoid open-redirect.
    private static bool IsLocalUrl(string url)
        => !string.IsNullOrEmpty(url)
           && url.StartsWith('/')
           && !url.StartsWith("//")
           && !url.StartsWith("/\\");
}
