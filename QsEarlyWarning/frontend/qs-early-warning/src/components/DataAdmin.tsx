import { useEffect, useState } from "react";
import { api, type EntityMeta, type EntityColumn, type EntityRow } from "../api/client";

type FkOptions = Record<string, { id: number; label: string }[]>;

export function DataAdmin({ rev, onChanged }: { rev: number; onChanged: () => void }) {
  const [metas, setMetas] = useState<EntityMeta[] | null>(null);
  const [selKey, setSelKey] = useState<string>("");
  const [rows, setRows] = useState<EntityRow[] | null>(null);
  const [fkOpts, setFkOpts] = useState<FkOptions>({});
  const [editing, setEditing] = useState<"new" | EntityRow | null>(null);
  const [form, setForm] = useState<Record<string, string>>({});
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);

  const meta = metas?.find((m) => m.key === selKey) ?? null;

  useEffect(() => {
    api.entities().then((m) => { setMetas(m); setSelKey((k) => k || m[0]?.key || ""); }).catch((e) => setErr(String(e.message ?? e)));
  }, []);

  async function loadRows(m: EntityMeta) {
    setErr(null); setEditing(null);
    try {
      setRows(await api.entityList(m.key));
      // load FK dropdown options for any FK columns
      const opts: FkOptions = { ...fkOpts };
      for (const c of m.columns.filter((c) => c.fkEntity)) {
        if (opts[c.fkEntity!]) continue;
        const fkMeta = metas!.find((x) => x.key === c.fkEntity);
        const fkRows = await api.entityList(c.fkEntity!);
        opts[c.fkEntity!] = fkRows.map((r) => ({
          id: Number(r.id),
          label: (fkMeta?.naturalKey ?? ["id"]).map((k) => String(r[k] ?? "")).join(" · ") || `#${r.id}`,
        }));
      }
      setFkOpts(opts);
    } catch (e: unknown) { setErr(String((e as Error).message ?? e)); }
  }

  useEffect(() => { if (meta) loadRows(meta); /* eslint-disable-next-line */ }, [selKey, rev, metas]);

  function startAdd() {
    if (!meta) return;
    const f: Record<string, string> = {};
    meta.columns.filter((c) => c.insertable).forEach((c) => { f[c.name] = ""; });
    setForm(f); setEditing("new"); setMsg(null); setErr(null);
  }
  function startEdit(row: EntityRow) {
    if (!meta) return;
    const f: Record<string, string> = {};
    meta.columns.filter((c) => c.updatable).forEach((c) => { f[c.name] = row[c.name] == null ? "" : String(row[c.name]); });
    setForm(f); setEditing(row); setMsg(null); setErr(null);
  }

  function bodyFromForm(cols: EntityColumn[]): EntityRow {
    const b: EntityRow = {};
    for (const c of cols) {
      const v = form[c.name];
      if (c.kind === "Bool") b[c.name] = v === "true";
      else if (v === undefined || v === "") { if (c.required) b[c.name] = ""; /* let DB reject */ }
      else b[c.name] = v;   // sent as string; the API coerces per column kind
    }
    return b;
  }

  async function save() {
    if (!meta) return;
    setMsg(null); setErr(null);
    try {
      if (editing === "new") {
        await api.entityCreate(meta.key, bodyFromForm(meta.columns.filter((c) => c.insertable)));
        setMsg("Created ✓");
      } else if (editing) {
        await api.entityUpdate(meta.key, Number(editing.id), bodyFromForm(meta.columns.filter((c) => c.updatable)));
        setMsg("Saved ✓");
      }
      setEditing(null); await loadRows(meta); onChanged();
    } catch (e: unknown) { setErr(String((e as Error).message ?? e)); }
  }

  async function del(row: EntityRow) {
    if (!meta || !confirm(`Delete ${meta.display} #${row.id}?`)) return;
    setMsg(null); setErr(null);
    try { await api.entityDelete(meta.key, Number(row.id)); await loadRows(meta); onChanged(); }
    catch (e: unknown) { setErr(String((e as Error).message ?? e)); }
  }

  if (!metas) return <div className="muted">Loading…</div>;

  const fkLabel = (c: EntityColumn, val: unknown) => {
    const opt = c.fkEntity && fkOpts[c.fkEntity]?.find((o) => o.id === Number(val));
    return opt ? opt.label : val == null ? "—" : String(val);
  };

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">DATA ADMIN</span>
        <select value={selKey} onChange={(e) => setSelKey(e.target.value)}>
          {metas.map((m) => <option key={m.key} value={m.key}>{m.display}</option>)}
        </select>
        {meta?.caps.create && <button className="btn-sm" onClick={startAdd}>+ Add</button>}
        <span className="muted small" style={{ marginLeft: "auto" }}>{rows?.length ?? 0} rows{meta && !meta.caps.create ? " · read-only" : ""}</span>
      </div>

      {err && <div className="error">{err}</div>}
      {msg && <div className="ok-msg">{msg}</div>}

      {editing && meta && (
        <div className="card narrow" style={{ margin: "10px 0" }}>
          <div className="panel-head"><b>{editing === "new" ? `New ${meta.display}` : `Edit #${(editing as EntityRow).id}`}</b></div>
          <div className="capture">
            {meta.columns.filter((c) => (editing === "new" ? c.insertable : c.updatable)).map((c) => (
              <label key={c.name}>{c.name}{c.required ? " *" : ""}
                {c.fkEntity ? (
                  <select value={form[c.name] ?? ""} onChange={(e) => setForm({ ...form, [c.name]: e.target.value })}>
                    <option value="">— select —</option>
                    {(fkOpts[c.fkEntity] ?? []).map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
                  </select>
                ) : c.enum ? (
                  <select value={form[c.name] ?? ""} onChange={(e) => setForm({ ...form, [c.name]: e.target.value })}>
                    <option value="">— select —</option>
                    {c.enum.map((v) => <option key={v} value={v}>{v}</option>)}
                  </select>
                ) : c.kind === "Bool" ? (
                  <input type="checkbox" checked={form[c.name] === "true"} onChange={(e) => setForm({ ...form, [c.name]: String(e.target.checked) })} />
                ) : (
                  <input type={c.kind === "Date" ? "date" : c.kind === "Text" ? "text" : "number"} step="any"
                    value={form[c.name] ?? ""} onChange={(e) => setForm({ ...form, [c.name]: e.target.value })} />
                )}
              </label>
            ))}
            <div style={{ display: "flex", gap: 8 }}>
              <button onClick={save}>{editing === "new" ? "Create" : "Save"}</button>
              <button className="btn-sm" style={{ background: "var(--surface-2)" }} onClick={() => setEditing(null)}>Cancel</button>
            </div>
          </div>
        </div>
      )}

      <div className="grid-scroll">
        <table className="grid">
          <thead>
            <tr>
              <th>id</th>
              {meta?.columns.map((c) => <th key={c.name}>{c.name}</th>)}
              {meta && (meta.caps.update || meta.caps.delete) && <th></th>}
            </tr>
          </thead>
          <tbody>
            {(rows ?? []).map((row) => (
              <tr key={String(row.id)}>
                <td className="mono">{String(row.id)}</td>
                {meta?.columns.map((c) => <td key={c.name} className={c.kind === "Numeric" || c.kind === "Int" || c.kind === "Bigint" ? "num" : ""}>{c.fkEntity ? fkLabel(c, row[c.name]) : row[c.name] == null ? "—" : String(row[c.name])}</td>)}
                {meta && (meta.caps.update || meta.caps.delete) && (
                  <td style={{ whiteSpace: "nowrap" }}>
                    {meta.caps.update && <button className="btn-sm" onClick={() => startEdit(row)}>Edit</button>}
                    {meta.caps.delete && <button className="btn-sm" style={{ marginLeft: 6, background: "var(--danger)" }} onClick={() => del(row)}>Del</button>}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
