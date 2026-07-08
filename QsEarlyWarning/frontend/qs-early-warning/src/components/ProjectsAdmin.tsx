import { useState } from "react";
import { api, type ImportSummary, type Project } from "../api/client";

type Msg = { ok: boolean; text: string } | null;
type NewMode = "empty" | "workbook";

function summaryText(s: ImportSummary): string {
  if (!s.activated) {
    const why = s.failureReason ?? "activation failed";
    const first = s.publishViolations?.[0];
    return `Import not activated — ${why}${first ? `: ${first}` : ""}.`;
  }
  return `Imported ✓ — ${s.costCentres} cost centres × ${s.periods} periods → ${s.facts} facts${s.passed ? "" : " (reconciliation had mismatches)"}.`;
}

export function ProjectsAdmin({ projects, onProjectsChanged }: { projects: Project[]; onProjectsChanged: () => void }) {
  const [adding, setAdding] = useState(false);
  const [newMode, setNewMode] = useState<NewMode>("workbook");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [currency, setCurrency] = useState("AED");
  const [file, setFile] = useState<File | null>(null);

  const [editSlug, setEditSlug] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editCurrency, setEditCurrency] = useState("");

  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<Msg>(null);

  function resetNew() {
    setAdding(false); setName(""); setSlug(""); setCurrency("AED"); setFile(null); setNewMode("workbook");
  }

  async function run<T>(fn: () => Promise<T>, ok: (r: T) => string): Promise<void> {
    setBusy(true); setMsg(null);
    try { const r = await fn(); setMsg({ ok: true, text: ok(r) }); onProjectsChanged(); }
    catch (e: unknown) { setMsg({ ok: false, text: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  }

  async function createProject() {
    const body = { name: name.trim(), slug: slug.trim(), currency: currency.trim() };
    if (newMode === "empty") {
      await run(() => api.createProject(body), (p) => `Created empty project “${p.name}”. Upload a workbook to populate it.`);
      resetNew();
    } else {
      if (!file) { setMsg({ ok: false, text: "Choose a workbook (.xlsx) to import." }); return; }
      await run(() => api.importProject(body, file), summaryText);
      resetNew();
    }
  }

  function startEdit(p: Project) {
    setEditSlug(p.slug); setEditName(p.name); setEditCurrency(p.reportingCurrency); setMsg(null);
  }
  async function saveEdit(p: Project) {
    // Only send fields that actually changed — avoids tripping the "currency immutable once data exists"
    // rule on a plain rename.
    const body: { name?: string; currency?: string } = {};
    if (editName.trim() && editName.trim() !== p.name) body.name = editName.trim();
    if (editCurrency.trim() && editCurrency.trim() !== p.reportingCurrency) body.currency = editCurrency.trim();
    if (!body.name && !body.currency) { setEditSlug(null); return; }
    await run(() => api.updateProject(p.slug, body), () => `Updated “${editName.trim()}”.`);
    setEditSlug(null);
  }

  async function reimport(p: Project, f: File) {
    if (!confirm(`Re-import “${p.name}” from ${f.name}? This REPLACES the project's existing data.`)) return;
    await run(() => api.reimportProject(p.slug, f), summaryText);
  }

  async function del(p: Project) {
    if (!confirm(`Delete “${p.name}” (${p.slug}) and all its data? This cannot be undone.`)) return;
    await run(() => api.deleteProject(p.slug), () => `Deleted “${p.name}”.`);
  }

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">PROJECTS</span>
        {!adding && <button className="btn btn-sm btn-primary" onClick={() => { setAdding(true); setMsg(null); }}>+ New Project</button>}
        <span className="muted small" style={{ marginLeft: "auto" }}>{projects.length} project{projects.length === 1 ? "" : "s"}</span>
      </div>

      {msg && <div className={msg.ok ? "ok-msg" : "error"}>{msg.text}</div>}

      {adding && (
        <div className="card narrow" style={{ margin: "10px 0" }}>
          <div className="panel-head"><b>New project</b></div>
          <div className="capture">
            <label>Mode&nbsp;
              <select value={newMode} onChange={(e) => setNewMode(e.target.value as NewMode)}>
                <option value="workbook">From workbook (.xlsx) — creates + populates</option>
                <option value="empty">Empty — add data later</option>
              </select>
            </label>
            <label>Name *
              <input type="text" value={name} placeholder="Tower Y" onChange={(e) => setName(e.target.value)} />
            </label>
            <label>Slug *
              <input type="text" value={slug} placeholder="tower-y" onChange={(e) => setSlug(e.target.value)} />
            </label>
            <label>Reporting currency *
              <input type="text" value={currency} placeholder="AED" maxLength={3}
                onChange={(e) => setCurrency(e.target.value.toUpperCase())} />
            </label>
            {newMode === "workbook" && (
              <label>Workbook *
                <input type="file" accept=".xlsx" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              </label>
            )}
            <div style={{ display: "flex", gap: 8 }}>
              <button className="btn btn-primary" onClick={createProject} disabled={busy}>{busy ? "Working…" : newMode === "empty" ? "Create" : "Create & import"}</button>
              <button className="btn btn-sm btn-secondary" onClick={resetNew} disabled={busy}>Cancel</button>
            </div>
          </div>
        </div>
      )}

      <div className="grid-scroll">
        <table className="grid">
          <thead>
            <tr><th>name</th><th>slug</th><th>currency</th><th>active est.</th><th>status</th><th></th></tr>
          </thead>
          <tbody>
            {projects.map((p) => {
              const editing = editSlug === p.slug;
              return (
                <tr key={p.slug}>
                  <td>{editing
                    ? <input type="text" value={editName} onChange={(e) => setEditName(e.target.value)} />
                    : p.name}</td>
                  <td className="mono">{p.slug}</td>
                  <td>{editing
                    ? <input type="text" value={editCurrency} maxLength={3} style={{ width: 60 }}
                        onChange={(e) => setEditCurrency(e.target.value.toUpperCase())} />
                    : p.reportingCurrency}</td>
                  <td className="num">{p.activeEstimateVersionId ?? "—"}</td>
                  <td>{p.activeEstimateVersionId == null
                    ? <span className="tag tag-amber">empty</span>
                    : <span className="tag tag-green">{p.ledgerActive ? "ledger" : "snapshot"}</span>}</td>
                  <td style={{ whiteSpace: "nowrap" }}>
                    {editing ? (
                      <>
                        <button className="btn btn-sm btn-primary" onClick={() => saveEdit(p)} disabled={busy}>Save</button>
                        <button className="btn btn-sm btn-secondary" style={{ marginLeft: 6 }} onClick={() => setEditSlug(null)}>Cancel</button>
                      </>
                    ) : (
                      <>
                        <button className="btn btn-sm btn-secondary" onClick={() => startEdit(p)} disabled={busy}>Rename</button>
                        <label className="btn btn-sm btn-secondary" style={{ marginLeft: 6, cursor: "pointer" }}>
                          Re-import
                          <input type="file" accept=".xlsx" style={{ display: "none" }} disabled={busy}
                            onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ""; if (f) reimport(p, f); }} />
                        </label>
                        <button className="btn btn-sm btn-danger" style={{ marginLeft: 6 }} onClick={() => del(p)} disabled={busy}>Delete</button>
                      </>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
