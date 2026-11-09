using Deming.Data.Models;
using Deming.Server.Exposure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// Shared plumbing of the account pages. Like <see cref="PanelPage"/>, a page
/// handles its named form post itself (static SSR): post → act → redirect, or render
/// again with <see cref="Error"/> and an error status code.
/// </summary>
public abstract class AccountPage : ComponentBase
{
    [Inject] protected IStringLocalizer<PanelText> L { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected SignInManager<OwnerAccount> SignIn { get; set; } = null!;
    [Inject] protected UserManager<OwnerAccount> Users { get; set; } = null!;
    [CascadingParameter] protected HttpContext Http { get; set; } = null!;

    /// <summary>Key of a success message (resource <c>Ok_&lt;key&gt;</c>) left by the redirect after a post.</summary>
    [SupplyParameterFromQuery(Name = "ok")] public string? Ok { get; set; }

    /// <summary>Localized error of the action that just failed; the page renders again with it.</summary>
    protected string? Error { get; set; }

    protected string? OkText
    {
        get
        {
            if (Ok is null) return null;
            var text = L["Ok_" + Ok];
            return text.ResourceNotFound ? null : text.Value;
        }
    }

    protected Task<IFormCollection> FormAsync() => Http.Request.ReadFormAsync(Http.RequestAborted);

    /// <summary>The text of a form field, trimmed; null when absent or blank.</summary>
    protected static string? Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;

    /// <summary>A form field as typed (passwords are not trimmed); empty when absent.</summary>
    protected static string Raw(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v) ? v.ToString() : "";

    protected static bool Checked(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v) && v.ToString() is "true" or "on";

    /// <summary>The page renders again with <paramref name="message"/> and <paramref name="status"/>.</summary>
    protected void Fail(string message, int status = StatusCodes.Status400BadRequest)
    {
        Error = message;
        Http.Response.StatusCode = status;
    }

    /// <summary>Identity's refusals in the panel's language; an unknown code keeps Identity's text.</summary>
    protected string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e =>
        {
            var text = e.Code == nameof(IdentityErrorDescriber.PasswordTooShort)
                ? L["Identity_PasswordTooShort", Users.Options.Password.RequiredLength]
                : L["Identity_" + e.Code];
            return text.ResourceNotFound ? e.Description : text.Value;
        }));

    /// <summary>Turnstile on the anonymous forms: off by default on loopback.</summary>
    protected async Task<bool> HumanAsync(IFormCollection form)
    {
        var options = Http.RequestServices.GetRequiredService<TurnstileOptions>();
        if (!options.Enabled)
            return true;
        var verifier = Http.RequestServices.GetRequiredService<ITurnstileVerifier>();
        return await verifier.VerifyAsync(form[PanelAuth.TurnstileField], Http.Connection.RemoteIpAddress?.ToString(), Http.RequestAborted);
    }

    /// <summary>The signed-in owner; a stale session (the account was replaced) is sent to the login.</summary>
    protected async Task<OwnerAccount?> OwnerAsync()
    {
        var user = await Users.GetUserAsync(Http.User);
        if (user is null)
            Nav.NavigateTo("/login");
        return user;
    }

    /// <summary>Redirect (post-redirect-get) to <paramref name="url"/> with a success message.</summary>
    protected void Done(string url, string okKey) =>
        Nav.NavigateTo(url + (url.Contains('?') ? '&' : '?') + "ok=" + Uri.EscapeDataString(okKey));
}
