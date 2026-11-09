-- OwnerAccount.Insert: the one owner. A second row fails ux_owner_account_singleton.
-- Params: OwnerAccount properties (@Id, @UserName, ...)
INSERT INTO owner_account (
    id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp,
    lockout_enabled, lockout_end, access_failed_count,
    two_factor_enabled, authenticator_key, recovery_code_hashes)
VALUES (
    @Id, @UserName, @NormalizedUserName, @PasswordHash, @SecurityStamp, @ConcurrencyStamp,
    @LockoutEnabled, @LockoutEnd, @AccessFailedCount,
    @TwoFactorEnabled, @AuthenticatorKey, @RecoveryCodeHashes);
