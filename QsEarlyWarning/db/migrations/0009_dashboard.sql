-- 0009_dashboard.sql — support the multi-project dashboard (plan §7 Phase 2, dashboard follow-through)
--
-- The tenant switcher needs to LIST the projects a user belongs to, but the base projects policy
-- (id = current_project AND member) only ever exposes the ONE selected project. Add a permissive
-- SELECT-only policy so a member can see the metadata of ALL their projects. Writes are unaffected
-- (still governed by the base FOR ALL policy's WITH CHECK), and the data tables (facts, ledger, …)
-- remain gated to the single selected project — this only widens read access to projects' own
-- metadata (slug/name/currency), which the switcher requires.

SET search_path = qs, public;

CREATE POLICY p_projects_member_select ON qs.projects
    FOR SELECT USING (qs.fn_is_member(id));
