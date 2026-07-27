-- Rate-gate refund (task 54): the minimum-play-time anti-cheat gate has been wrongly unranking
-- every up-rate play since rate mods became rankable at any speed (task 27).
--
-- THE BUG. Both submission paths compared a play's elapsed WALL CLOCK (score-token creation to
-- submission) against a bound expressed in MAP time: 0.9 x drain, and since task 47
-- 0.9 x (drain - skippable). A rate mod is precisely the conversion between the two. A map played
-- at the default Double Time 1.5x takes drain / 1.5 = 0.667 x drain of real time, which is below
-- 0.9 x drain, so no DT play could clear the gate at all; every rate above roughly 1.111x was
-- stored unranked however honest it was. The corrected bound is
--
--     0.9 x (drain - skippable) / rate
--
-- where rate is the submitted speed_change of DT/NC/HT (1.0 with no rate mod, and the browser
-- player always). Down-rates were never victims: Half Time takes MORE real time than 1.0x, so the
-- un-divided bound only ever asked less of it than it should have. See Scoring/PlayTimeGate.cs.
--
-- WHAT THIS FILE DOES, AND WHAT IT DELIBERATELY DOES NOT
-- No schema is needed: 016_refund_skip_gate.sql already created score_refunds, keyed by migration
-- exactly so a later refund gets its own key and its own guard, and this one uses
-- '017_rate_gate_refund'. The refund itself is NOT expressible here for the reasons 016 sets out:
-- deciding "was this row unranked BY THE GATE and by nothing else" needs the submit path's own
-- recompute (ScoringContract, ModMultiplier) and its own reading of a stored mod stack's
-- speed_change (RateMods), all of which exist in C# and must not be mirrored a third time in SQL.
-- So this file is the documentation and the schema_migrations marker; Scoring/RateGateRefund.cs
-- performs the refund at startup, right after Scoring/SkipGateRefund.cs (Program.cs).
--
-- The statement below is a comment on the existing table, chosen because the migration runner
-- executes each file as SQL and this pass genuinely has nothing to alter: it records what the
-- second key in the table means, where an operator reviewing a refund will actually look.
COMMENT ON TABLE score_refunds IS
    'Guard + audit trail for scores whose ranked flag a play-time-gate correction restored. '
    'One row per (migration, score) with the elapsed/required numbers the decision was made on. '
    'Keys so far: 016_refund_skip_gate (the gate ignored the in-game skip button), '
    '017_rate_gate_refund (the gate ignored the play rate of DT/NC/HT).';
