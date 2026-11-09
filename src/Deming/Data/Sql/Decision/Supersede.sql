-- Decision.Supersede: marks a decision superseded by another one. One-way: a decision that is
-- already superseded raises 'decision_already_superseded' (trigger tr_decision_supersede).
-- Params: @Id, @ReplacementId, @Now
UPDATE decision
SET status = 'superseded',
    superseded_by_id = @ReplacementId,
    superseded_at = @Now
WHERE id = @Id
RETURNING *;
