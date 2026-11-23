using System.ComponentModel.DataAnnotations;
using GetLaps.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Identity.Web;

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
