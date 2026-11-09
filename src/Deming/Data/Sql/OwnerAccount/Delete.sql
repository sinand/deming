-- OwnerAccount.Delete: with the concurrency check, like Update.
-- Params: @Id, @ConcurrencyStamp
DELETE FROM owner_account
WHERE id = @Id
  AND concurrency_stamp IS @ConcurrencyStamp;
