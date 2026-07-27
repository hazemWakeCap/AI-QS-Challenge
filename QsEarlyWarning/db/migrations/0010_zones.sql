-- 0010_zones.sql — carry the workbook's physical-location tag into the model (Phase 2, Cost X-Ray)
--
-- `9_HISTORICAL_DATA.Zone_Area` is the ONLY spatial attribute in the whole dataset
-- (STRUCTURE, FLOORS-ALL, FLOORS-B2-RF, BASEMENT, ALL-RISERS, EXTERNAL-FACADE, SITE-WIDE, …).
-- Phase 1 read it and threw it away, so the product could say WHICH cost centre was drifting but
-- never WHERE in the building. This column is what lets a cost centre be painted onto geometry.
--
-- Design note — compound zones are kept whole, not split.
-- Five centres carry `BASEMENT+EXT`. We deliberately do NOT model this as a many-to-many zone tag
-- set, because splitting a centre's BAC across two zones would require an allocation ratio that
-- the data does not contain — inventing one would put fabricated money on screen. A compound code
-- is stored as its own zone; the viewer maps that one zone onto two regions of geometry, while the
-- money stays undivided. Consequence: SUM(bac) GROUP BY zone_code ties out exactly to project BAC.
--
-- Nullable by design: a workbook without the column imports fine and its money surfaces in the
-- cost-map's explicit `unmappedBac` residual rather than silently disappearing.

SET search_path = qs, public;

ALTER TABLE qs.cost_centres ADD COLUMN IF NOT EXISTS zone_code text;

COMMENT ON COLUMN qs.cost_centres.zone_code IS
    'Physical location tag from 9_HISTORICAL_DATA.Zone_Area. Compound codes (BASEMENT+EXT) are '
    'stored verbatim — never split — so zone rollups tie out to project BAC. NULL = un-located.';

-- Zone rollups are the read pattern (GROUP BY zone_code within a project).
CREATE INDEX IF NOT EXISTS ix_cost_centres_zone ON qs.cost_centres (project_id, zone_code);
