-- OwnerAccount.Update: optimistic concurrency. 0 rows changed = someone else updated first.
-- Params: OwnerAccount properties (with the NEW @ConcurrencyStamp), @OldConcurrencyStamp
UPDATE owner_account
SET user_name            = @UserName,
    normalized_user_name = @NormalizedUserName,
    password_hash        = @PasswordHash,
    security_stamp       = @SecurityStamp,
    concurrency_stamp    = @ConcurrencyStamp,
    lockout_enabled      = @LockoutEnabled,
    lockout_end          = @LockoutEnd,
    access_failed_count  = @AccessFailedCount,
    two_factor_enabled   = @TwoFactorEnabled,
    authenticator_key    = @AuthenticatorKey,
    recovery_code_hashes = @RecoveryCodeHashes
WHERE id = @Id
  AND concurrency_stamp IS @OldConcurrencyStamp;
