# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately:

- on GitHub: the **Security** tab of this repository, **Report a vulnerability**, or
- by e-mail to the address under "License and contact" in the [README](README.md).

Say what you found, how to reproduce it and which version (`deming version`) you used. You
will get an answer; this is a one-person project, so allow a few days.

## Supported versions

Only the latest release gets fixes.

## What Deming protects, and what it does not

Deming is built to run on the machine of the person who uses it.

- It listens on the loopback address by default and serves HTTPS only. It refuses to start on
  another address unless an IP allow-list, real Turnstile keys and HTTPS are configured.
- The API needs the key in the user's config file; the panel needs the owner's login, on
  localhost too.
- Anyone who can read the user's files can read the database, the API key and the HTTPS
  certificate's private key: they are protected by file permissions, not by encryption.
- `deming trust` adds Deming's own certificate (a leaf for `localhost`, not a certificate
  authority) to the user's trust store. `deming serve` never does that by itself.

Reports about any of these boundaries being crossed are welcome, and so are reports about the
release binaries and packages.
