-- Project.GetByName: the name from .deming, matched after normalization ("Köprü" = "kopru").
-- Params: @Name
SELECT *
FROM project
WHERE name_normalized = deming_normalize(trim(@Name));
