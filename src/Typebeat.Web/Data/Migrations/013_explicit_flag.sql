-- Explicit-content marker, chosen by the creator in the in-game submission wizard alongside the
-- pending/unranked target and carried on the PUT /bss/beatmapsets metadata call as the optional
-- JSON boolean "explicit" (absent = false, which is what every pre-toggle client sends).
--
-- Purely a display flag: it gates nothing (no filtering out of listings, no download block), it
-- only makes the site render an EXPLICIT badge next to the set title the way osu does. Existing
-- rows default to false; a creator flips it by re-submitting with the toggle on.
ALTER TABLE beatmapsets
    ADD COLUMN explicit boolean NOT NULL DEFAULT false;
