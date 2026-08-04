# OIDC / SSO [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

InfiniDysk can authenticate browser sessions with any standards-compliant OpenID
Connect (OIDC) provider, including Authentik, Authelia, and Keycloak. OIDC is
disabled by default and does not change WebDAV Basic authentication or API-key
authentication.

OIDC users are not written to the InfiniDysk database. A successful identity
provider login creates the same signed frontend session used by local login,
using a configured ID-token claim as the displayed username.

## Enable OIDC

Register InfiniDysk as a confidential OIDC client in your identity provider, then
set these variables on the InfiniDysk container:

```yaml
environment:
  OIDC_ISSUER: https://auth.example.com/application/o/nzbdav/
  OIDC_CLIENT_ID: nzbdav
  OIDC_CLIENT_SECRET: replace-with-your-client-secret
  OIDC_REDIRECT_URI: https://nzbdav.example.com/auth/oidc/callback
```

Restart InfiniDysk after changing OIDC variables. The login page will show both
local credentials and **Sign in with SSO**.

`OIDC_ISSUER`, `OIDC_CLIENT_ID`, and `OIDC_CLIENT_SECRET` must all be set.
Missing any required value leaves OIDC disabled so local login remains
available.

!!! tip "Set the redirect URI explicitly"

    `OIDC_REDIRECT_URI` falls back to the configured SABnzbd **Base URL**, then
    to the incoming request origin. An explicit public HTTPS URL avoids
    reverse-proxy host or scheme mismatches. Register the exact same URI in the
    identity provider.

## Environment variable reference

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `OIDC_ISSUER` | Yes | — | OIDC issuer identifier. InfiniDysk discovers provider metadata from this issuer. |
| `OIDC_CLIENT_ID` | Yes | — | Confidential client identifier registered with the provider. |
| `OIDC_CLIENT_SECRET` | Yes | — | Confidential client secret. Keep it out of logs and source control. |
| `OIDC_REDIRECT_URI` | Recommended | `general.base-url`, then incoming request origin, plus `/auth/oidc/callback` | Public callback URI registered with the provider. |
| `OIDC_SCOPES` | No | `openid profile email` | Space-separated scopes requested during login. Must include `openid`. |
| `OIDC_USERNAME_CLAIM` | No | `preferred_username` | ID-token claim used as the displayed username. InfiniDysk falls back to `preferred_username`, `email`, then `sub`. |
| `OIDC_ADMIN_CLAIM` | No | — | Claim inspected to decide whether the session is admin or read-only. If omitted, all OIDC users are admins. |
| `OIDC_ADMIN_CLAIM_VALUE` | With `OIDC_ADMIN_CLAIM` | — | Exact string or array member that grants the admin role. If the claim is configured without a value, all OIDC users are read-only. |

These are frontend process variables, not `NZBDAV_CONFIG__...` settings. They
are read when the frontend starts and are not stored in SQLite or displayed in
the Settings UI.

## Map admin and read-only roles

To grant admin access only to members of an identity-provider group:

```yaml
environment:
  OIDC_ADMIN_CLAIM: groups
  OIDC_ADMIN_CLAIM_VALUE: nzbdav-admins
```

InfiniDysk accepts either a string claim or an array claim. A matching value grants
`admin`; every other authenticated OIDC user receives `readonly`.

!!! warning "Read-only is a UI role"

    In 0.10.0, read-only sessions can view the dashboard and settings but
    mutation controls are disabled or hidden. This is not backend API
    authorization: the frontend still uses its shared backend API key for
    authenticated sessions. Restrict access at the identity provider and do
    not treat the read-only role as a security boundary.

Local username/password sessions remain admins. OIDC role mapping does not
change WebDAV credentials, SABnzbd API keys, or other machine-client access.

## Identity provider setup

### Authentik

1. Create an OAuth2/OpenID Provider and a linked application.
2. Use a confidential client and the Authorization Code flow.
3. Add the exact InfiniDysk callback URI as a strict redirect URI.
4. Include `openid`, `profile`, and `email` scopes.
5. To map roles, expose group membership in the ID token and configure
   `OIDC_ADMIN_CLAIM=groups`.
6. Use the provider's issuer URL for `OIDC_ISSUER`; do not use only its
   authorization endpoint.

### Authelia

1. Add InfiniDysk under `identity_providers.oidc.clients` as a confidential client.
2. Register the exact callback URI and enable the Authorization Code flow.
3. Allow the `openid`, `profile`, and `email` scopes.
4. Configure the groups claim when using group-based role mapping.
5. Set `OIDC_ISSUER` to Authelia's published issuer URL.

### Keycloak

1. Create a client with OpenID Connect, client authentication enabled, and the
   Standard Flow enabled.
2. Add the exact callback URI to **Valid redirect URIs**.
3. Copy the client secret from the client's credentials.
4. Add a group-membership or role mapper to the ID token when using role
   mapping. Set its token claim name to the value used by
   `OIDC_ADMIN_CLAIM`.
5. Use the realm issuer URL, such as
   `https://keycloak.example.com/realms/media`.

## First run and local login

When OIDC is enabled, InfiniDysk skips local-account onboarding. The first
successful OIDC login creates a session directly without creating an
`Accounts` row. Local login remains visible and works if a local admin account
already exists.

Application logout clears the InfiniDysk session only. It does not currently end
the identity-provider session, so selecting SSO again may sign in immediately.

## Troubleshooting

### The SSO button is missing

Confirm all three required variables are non-empty and restart the container.
The logs identify missing variable names without printing their values.

### Redirect URI mismatch

The callback URI is case-sensitive and must match exactly in InfiniDysk and the
identity provider, including scheme, host, port, path, and any trailing slash.
Prefer setting `OIDC_REDIRECT_URI` explicitly when InfiniDysk is behind a reverse
proxy.

### SSO returns to the login page

Check the frontend logs for the single-line `OIDC sign-in failed` warning.
Common causes are an incorrect issuer or client secret, an expired login flow,
or an identity provider that did not return ID-token claims.

### The wrong username is displayed

Set `OIDC_USERNAME_CLAIM` to a string-valued ID-token claim exposed by the
provider. If that claim is absent or empty, InfiniDysk tries
`preferred_username`, `email`, and `sub` in that order.

### Every user is read-only

Verify that both `OIDC_ADMIN_CLAIM` and `OIDC_ADMIN_CLAIM_VALUE` are set, and
that the selected claim is included in the ID token. Claim names and values are
case-sensitive.
