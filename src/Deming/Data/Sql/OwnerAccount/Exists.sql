-- OwnerAccount.Exists: 1 when the owner account has been created (first visit creates it).
SELECT EXISTS (SELECT 1 FROM owner_account);
