-- ---------------------------------------------------------------------------------------
-- owner_account: two-factor sign-in (full ASP.NET Core Identity)
-- ---------------------------------------------------------------------------------------
-- two_factor_enabled   : IUserTwoFactorStore flag.
-- authenticator_key    : IUserAuthenticatorKeyStore, the TOTP shared secret (base32). Stored as
--                        is: the Data Protection keys live in this same file, so
--                        encrypting it with them would protect nothing.
-- recovery_code_hashes : IUserTwoFactorRecoveryCodeStore. SHA-256 (hex) of each unused code,
--                        ';'-separated; the plain codes are shown once and never stored.
--
-- Two-factor sign-in cannot be on without an authenticator key: Identity sets the key first
-- (ResetAuthenticatorKeyAsync) and only then enables two-factor.

ALTER TABLE owner_account ADD COLUMN two_factor_enabled INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_owner_account_two_factor_flag CHECK (two_factor_enabled IN (0, 1));

ALTER TABLE owner_account ADD COLUMN authenticator_key TEXT
    CONSTRAINT ck_owner_account_two_factor_key CHECK (two_factor_enabled = 0 OR authenticator_key IS NOT NULL);

ALTER TABLE owner_account ADD COLUMN recovery_code_hashes TEXT;
