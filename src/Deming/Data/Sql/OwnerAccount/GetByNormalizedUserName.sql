-- OwnerAccount.GetByNormalizedUserName (Identity's ILookupNormalizer output)
-- Params: @NormalizedUserName
SELECT *
FROM owner_account
WHERE normalized_user_name = @NormalizedUserName;
