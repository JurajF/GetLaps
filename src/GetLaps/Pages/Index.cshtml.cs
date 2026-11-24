using System.ComponentModel.DataAnnotations;
using GetLaps.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GetLaps.Pages;

// Turns "consent / sign-in required" errors from token acquisition into an Entra redirect.
[AuthorizeForScopes(Scopes = [LapsService.Scope])]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class IndexModel(LapsService laps, ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    [Display(Name = "PC name")]
    [Required(ErrorMessage = "Enter a PC name.")]
    [StringLength(255)]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\- ]*$",
        ErrorMessage = "Use letters, digits, dots, dashes, underscores or spaces only.")]
    public string? ComputerName { get; set; }

    public IReadOnlyList<LapsDevice>? Matches { get; private set; }

    public LapsDeviceCredentials? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? Notice { get; private set; }

    private string UserName => User.FindFirst("preferred_username")?.Value ?? User.Identity?.Name ?? "unknown";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostLookupAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var name = ComputerName!.Trim();
        try
        {
            var devices = await laps.FindDevicesAsync(name, ct);
            switch (devices.Count)
            {
                case 0:
                    ErrorMessage = $"No device named \"{name}\" has a LAPS password backed up to Entra ID.";
                    break;
                case 1:
                    await LoadCredentialsAsync(devices[0].Id, ct);
                    break;
                default:
                    Matches = devices;
                    break;
            }
        }
        catch (LapsException ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeviceAsync(string deviceId, CancellationToken ct)
    {
        ModelState.Clear();
        if (!Guid.TryParse(deviceId, out _))
        {
            ErrorMessage = "Invalid device ID.";
            return Page();
        }

        try
        {
            await LoadCredentialsAsync(deviceId, ct);
            ComputerName = Result?.Device.DeviceName;
        }
        catch (LapsException ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRotateAsync(string deviceId, CancellationToken ct)
    {
        ModelState.Clear();
        if (!Guid.TryParse(deviceId, out _))
        {
            ErrorMessage = "Invalid device ID.";
            return Page();
        }

        try
        {
            var intuneName = await laps.RequestRotationAsync(deviceId, ct);

            // Audit trail: who asked for which device's password to be rotated.
            logger.LogInformation("LAPS password rotation for device {DeviceName} ({DeviceId}) requested by {User}",
                intuneName, deviceId, UserName);

            Notice = $"Rotation requested for {intuneName}. Intune sends it at the device's next check-in " +
                     "(usually within minutes if the PC is online). Once the device backs up its new password, " +
                     "look it up again to see it.";
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            // The rotation scopes have not been granted to this session yet: send the user to
            // Entra to consent, then back here. They will need to click the button again.
            logger.LogInformation(ex, "Incremental consent needed for LAPS rotation for {User}", UserName);
            return ConsentChallenge();
        }
        catch (LapsException ex)
        {
            ErrorMessage = ex.Message;
        }

        try
        {
            await LoadCredentialsAsync(deviceId, ct);
            ComputerName = Result?.Device.DeviceName;
        }
        catch (LapsException ex)
        {
            ErrorMessage ??= ex.Message;
        }

        return Page();
    }

    private ChallengeResult ConsentChallenge()
    {
        var properties = new AuthenticationProperties { RedirectUri = Url.Page("/Index") };
        properties.SetParameter<ICollection<string>>(OpenIdConnectParameterNames.Scope,
            ["openid", "profile", "offline_access", .. LapsService.Scopes, .. LapsService.RotationScopes]);

        var loginHint = User.GetLoginHint();
        if (!string.IsNullOrEmpty(loginHint))
        {
            properties.SetParameter(OpenIdConnectParameterNames.LoginHint, loginHint);
        }

        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }

    private async Task LoadCredentialsAsync(string deviceId, CancellationToken ct)
    {
        Result = await laps.GetCredentialsAsync(deviceId, ct);

        // Audit trail: who read which device's password. The password itself is never logged.
        logger.LogInformation("LAPS credentials for device {DeviceName} ({DeviceId}) retrieved by {User}",
            Result.Device.DeviceName, Result.Device.Id, UserName);

        if (Result.Current is null)
        {
            ErrorMessage = "The device exists in Entra ID but has no LAPS credentials stored.";
        }
    }
}
