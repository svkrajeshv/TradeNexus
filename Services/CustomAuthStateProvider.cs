using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace NexusApp.Services;

/// <summary>
/// Cookie-backed authentication state provider. It surfaces the
/// <see cref="ClaimsPrincipal"/> that ASP.NET Core has already materialised from
/// the auth cookie on <see cref="HttpContext.User"/>. Because the cookie is read
/// server-side (including during prerendering) this avoids the login/dashboard
/// flip-flop that browser-storage based providers suffer from.
///
/// Sign-in / sign-out are performed by the /auth/login and /auth/logout minimal
/// API endpoints (see <c>Startup\AuthEndpoints.cs</c>), which is the only place a
/// cookie can be written in Blazor Server.
/// </summary>
public class CustomAuthStateProvider(IHttpContextAccessor httpContextAccessor) : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User ?? Anonymous;
        return Task.FromResult(new AuthenticationState(user));
    }
}
