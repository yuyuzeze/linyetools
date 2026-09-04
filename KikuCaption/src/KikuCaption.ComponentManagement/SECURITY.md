# KikuCaption.ComponentManagement — Security Model (R7A / R7A.1)

This library fetches a remote manifest and downloads/extracts components (models, FFmpeg, LibVLC).
The trust model is documented here so later stages build on it correctly.

## What each control provides

- **SHA-256** verification proves **integrity only** — that the bytes received are exactly the bytes
  the manifest named. It does **NOT** prove **authenticity**: anyone who can write the manifest can put
  a matching hash next to a malicious file. SHA-256 defends against corruption and tampering *in
  transit relative to the manifest*, not against a compromised or spoofed publisher.

- **Publisher authenticity depends entirely on trusted HTTPS.** The manifest and every component/app
  URL must be HTTPS (plain HTTP only via an explicit `AllowInsecureHttp` opt-in for a trusted internal
  test). Redirects are followed under our own control and re-validated at every hop; an HTTPS→HTTP
  downgrade is always refused. So the security of the whole chain reduces to: *the manifest is served
  from an HTTPS origin the organization trusts, with a valid certificate.*

- **Manifest identity** (`product` + `channel`) is checked against the app's expected values before any
  component is downloaded, so a manifest for a different product/channel cannot drive a download.

- **Path & archive safety**: install directories and requiredFiles are safe relative paths that resolve
  inside the component directory; archive extraction rejects traversal, absolute/UNC paths, symlinks,
  and enforces entry-count and total-size (zip-bomb) limits.

## Known gap — deferred to R7D (do NOT implement early)

There is currently **no cryptographic proof of publisher identity** beyond HTTPS transport trust.
R7D should add one of:

- a **signed manifest** (e.g. a detached signature over the manifest verified against a pinned public
  key baked into the app), and/or
- **Authenticode verification** of the downloaded application package / executables before install.

Until then, treat the manifest origin as the root of trust and keep it on a controlled, HTTPS-only,
organization-approved host.
