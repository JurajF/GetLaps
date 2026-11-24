# GetLaps

A small ASP.NET Core (.NET 10) web app for helpdesk staff. You sign in with your
Microsoft Entra ID account, type a PC name, and the app shows that device's Windows LAPS
local administrator account and password, read from Microsoft Graph with **your own
(delegated) permissions**. The app never holds standing access to passwords: if your
account can't read LAPS passwords in Entra, neither can the app on your behalf.

## How it works

1. You sign in interactively (OpenID Connect via `Microsoft.Identity.Web`). The app asks for
   the delegated Graph permission `DeviceLocalCredential.Read.All` at sign-in.
2. You enter a PC name. The app calls
   `GET /directory/deviceLocalCredentials?$filter=deviceName eq '<name>'` to find the device.
3. It then calls `GET /directory/deviceLocalCredentials/{deviceId}?$select=credentials`
   and decodes the base64 password. The newest password is shown first, masked, with
   show and copy buttons; older backed-up passwords are listed below it.
4. If several devices share the name, you pick one by device ID.
5. Optional: **Request password rotation** asks Intune to rotate the device's local admin
   password. The app finds the Intune device with
   `GET /deviceManagement/managedDevices?$filter=azureADDeviceId eq '<deviceId>'` and calls
   `POST /beta/deviceManagement/managedDevices/{id}/rotateLocalAdminPassword` (beta only).
   Intune delivers it at the device's next check-in, and the new password shows up here once
   the device backs it up. Only Intune-managed devices can be rotated this way. The extra
   permissions are requested the first time someone clicks the button.

Every retrieval is logged as `LAPS credentials for device {name} ({id}) retrieved by {user}`,
and every rotation request as `LAPS password rotation for device {name} ({id}) requested by {user}`.
Passwords are never logged, and the result page is sent with `Cache-Control: no-store`.

## Prerequisites in your tenant

- Windows LAPS is enabled for Entra: **Entra admin center > Devices > Device settings >
  Enable Microsoft Entra Local Administrator Password Solution (LAPS) = Yes**.
- Devices have a LAPS policy (Intune endpoint security or GPO) with backup directory set
  to **Microsoft Entra ID**.
- People who use the app hold a role that can read LAPS passwords, for example
  **Cloud Device Administrator** or **Intune Administrator**, or a custom role with
  `microsoft.directory/deviceLocalCredentials/password/read`. (Global Reader can see LAPS
  metadata but not passwords, so lookups fail with "Access denied".)

## Entra app registration

1. **Entra admin center > Identity > Applications > App registrations > New registration**
   - Name: `GetLaps`
   - Supported account types: *Accounts in this organizational directory only*
   - Redirect URI: platform **Web**, `https://<your-host>/signin-oidc`
2. **Authentication** (on the new app)
   - Add more **Web** redirect URIs as needed:
     - `https://<your-host>/signout-callback-oidc`
     - Local Docker: `http://localhost:8080/signin-oidc` and `http://localhost:8080/signout-callback-oidc`
     - `dotnet run`: `https://localhost:7154/signin-oidc` and `https://localhost:7154/signout-callback-oidc`
   - Front-channel logout URL: `https://<your-host>/signout-oidc`
   - Leave *Access tokens* and *ID tokens* (implicit grant) **unchecked**. The app uses the
     authorization code flow.
3. **Certificates & secrets > Client secrets > New client secret.** Copy the value now;
   it is the `AzureAd__ClientSecret` setting.
4. **API permissions > Add a permission > Microsoft Graph > Delegated permissions**
   - `DeviceLocalCredential.Read.All`
   - `User.Read` (added by default)
   - Click **Grant admin consent for <tenant>**. `DeviceLocalCredential.Read.All` requires
     admin consent; users cannot consent to it themselves.
   - For the rotation button, also add `DeviceManagementManagedDevices.Read.All` and
     `DeviceManagementManagedDevices.PrivilegedOperations.All`, then grant admin consent
     again. Users who rotate also need an Intune role with the *Rotate local admin password*
     permission (for example Intune Administrator).
5. Recommended: limit who can sign in. Go to **Enterprise applications > GetLaps >
   Properties**, set **Assignment required** to **Yes**, then under **Users and groups**
   assign your helpdesk group.
6. Note the **Application (client) ID** and **Directory (tenant) ID** from the app's
   Overview page.

## Configuration

Nothing tenant-specific is in the code. Settings come from standard ASP.NET Core
configuration (appsettings, environment variables, user secrets). Environment variables use
`__` as the section separator.

| Setting | Environment variable | Required | Notes |
| --- | --- | --- | --- |
| `AzureAd:TenantId` | `AzureAd__TenantId` | yes | Directory (tenant) ID |
| `AzureAd:ClientId` | `AzureAd__ClientId` | yes | Application (client) ID |
| `AzureAd:ClientSecret` | `AzureAd__ClientSecret` | yes | Client secret value. Keep it out of source control. |
| `AzureAd:Instance` | `AzureAd__Instance` | no | Default `https://login.microsoftonline.com/` |
| `Graph:BaseUrl` | `Graph__BaseUrl` | no | Default `https://graph.microsoft.com/v1.0/` |
| `ReverseProxy:Enabled` | `ReverseProxy__Enabled` | no | `true` behind a TLS-terminating proxy, so redirect URIs use `https` |
| `DataProtection:KeysPath` | `DataProtection__KeysPath` | no | Folder for cookie encryption keys. The Docker image sets `/keys`. |

Prefer a certificate over a secret in production? `Microsoft.Identity.Web` supports
`AzureAd:ClientCertificates` (for example from Key Vault) instead of `ClientSecret`; see
[the credential docs](https://learn.microsoft.com/entra/msal/dotnet/microsoft-identity-web/certificates).

## Run locally

```bash
cd src/GetLaps
dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>"
dotnet user-secrets set "AzureAd:ClientId" "<client-id>"
dotnet user-secrets set "AzureAd:ClientSecret" "<client-secret>"
dotnet run --launch-profile https
```

Then open https://localhost:7154.

## Run with Docker

```bash
cp .env.example .env    # fill in tenant ID, client ID, client secret
docker compose up --build
```

Then open http://localhost:8080. (Edge and Chrome allow the sign-in cookies on
`http://localhost`; anywhere else, serve the app over HTTPS.)

Or without compose:

```bash
docker build -t getlaps .
docker run -p 8080:8080 \
  -e AzureAd__TenantId=<tenant-id> \
  -e AzureAd__ClientId=<client-id> \
  -e AzureAd__ClientSecret=<client-secret> \
  -v getlaps-keys:/keys \
  getlaps
```

The container listens on port 8080 as the non-root `app` user and exposes `/healthz`
for health probes.

### Production notes

- Put the container behind HTTPS (Azure Container Apps, App Service, nginx, Traefik...),
  set `ReverseProxy__Enabled=true`, and register `https://<your-host>/signin-oidc`.
- Mount a volume at `/keys` so sign-in cookies survive restarts. If you run more than one
  replica, share that key store (or use Azure Blob / Key Vault data protection providers).
- Tokens are cached in memory. After a restart users are sent back through sign-in once,
  which is silent if their Entra session is still valid.
- Send the app's logs to your SIEM to keep the audit trail of who looked up which device.
