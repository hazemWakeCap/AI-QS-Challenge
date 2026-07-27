import { useEffect, useMemo, useRef, useState } from "react";
import { api, type CostCentreEvm, type CostMap, type GeometrySpec } from "../api/client";
import { millions, money, ratio, DASH } from "../format";
import { hex, legendFor, paintByCost, type PaintMode } from "../model/costPaint";
import { generateTower, type GeneratedTower } from "../model/towerGenerator";
import { createViewer, fitToBounds, type Viewer } from "../model/viewer";
import { Spinner } from "./Loading";

/**
 * 3D Cost X-Ray — the watchlist, on the building.
 *
 * Two things this view is careful about:
 *  1. The geometry is generated, not surveyed. The provenance panel says so and shows the BOQ
 *     line behind every dimension.
 *  2. A green zone is not a safe zone. FLOORS-ALL rolls up to CPI 0.961 while holding 11 AMBER
 *     cost centres and AED 43.5M of unspent budget, so zones carrying AMBER centres are painted
 *     distinctly and the table shows the centre count next to the rollup.
 */
export function ModelView({
  period,
  rev,
  onSelectCentre,
}: {
  period: number;
  rev: number;
  /** Hands the full row to the existing cost-centre drawer — same contract as CostCentreGrid. */
  onSelectCentre?: (centre: CostCentreEvm) => void;
}) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewerRef = useRef<Viewer | null>(null);
  const towerRef = useRef<GeneratedTower | null>(null);

  const [spec, setSpec] = useState<GeometrySpec | null>(null);
  const [map, setMap] = useState<CostMap | null>(null);
  // Kept so a zone click can hand the drawer the same row shape CostCentreGrid does.
  const [centres, setCentres] = useState<CostCentreEvm[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const [mode, setMode] = useState<PaintMode>("cpi");
  const [selected, setSelected] = useState<string | null>(null);
  const [showProvenance, setShowProvenance] = useState(false);

  // Latest paint inputs, readable from inside the geometry effect without making geometry depend
  // on them: a rebuild must paint itself immediately, or it renders in its base grey until the
  // next unrelated change.
  const paintStateRef = useRef({ map, mode, selected });
  paintStateRef.current = { map, mode, selected };

  // ── data ──
  useEffect(() => {
    let off = false;
    setErr(null);
    Promise.all([api.geometrySpec(), api.costMap(period), api.costCentres(period)])
      .then(([g, m, c]) => {
        if (off) return;
        setSpec(g);
        setMap(m);
        setCentres(c);
      })
      .catch((e) => !off && setErr(String(e.message ?? e)));
    return () => {
      off = true;
    };
  }, [period, rev]);

  // ── viewer + geometry lifecycle ──
  // One effect owns both, so there is never a window where a live tower points at a disposed
  // viewer. StrictMode mounts, unmounts and remounts in development: reusing a viewer across that
  // cycle left the second mount talking to a disposed world, so each run builds its own and tears
  // it down completely. Geometry depends only on the spec — cost data repaints it, never rebuilds.
  useEffect(() => {
    if (!spec) return;
    const host = hostRef.current;
    if (!host) return;

    let cancelled = false;
    let owned: Viewer | null = null;

    (async () => {
      try {
        const viewer = await createViewer(host);
        if (cancelled) {
          viewer.dispose();
          return;
        }
        owned = viewer;
        viewerRef.current = viewer;

        const tower = generateTower(viewer, spec);
        towerRef.current = tower;

        const { map: m, mode: md, selected: sel } = paintStateRef.current;
        if (m) paintByCost(viewer, tower, m.zones, md, sel);

        await fitToBounds(viewer, tower.bounds);
        if (!cancelled) setReady(true);
      } catch (e) {
        if (!cancelled) setErr(`3D viewer failed to start: ${String((e as Error).message ?? e)}`);
      }
    })();

    return () => {
      cancelled = true;
      setReady(false);
      towerRef.current?.dispose();
      towerRef.current = null;
      viewerRef.current = null;
      owned?.dispose();
      // SimpleRenderer appends its own canvas; drop any that outlive the world.
      host.querySelectorAll("canvas").forEach((c) => c.remove());
    };
  }, [spec]);

  // ── repaint on data / mode / selection change ──
  useEffect(() => {
    if (!ready || !map || !towerRef.current || !viewerRef.current) return;
    paintByCost(viewerRef.current, towerRef.current, map.zones, mode, selected);
  }, [ready, map, mode, selected]);

  const zonesDrawn = towerRef.current?.byZone;
  const undrawn = useMemo(
    () => (map && zonesDrawn ? map.zones.filter((z) => !zonesDrawn.has(z.zoneCode)) : []),
    [map, zonesDrawn],
  );

  if (err) return <div className="error">{err}</div>;
  if (!spec || !map) return <Spinner />;

  const tieOut = map.zones.reduce((s, z) => s + z.bac, 0) + map.unmappedBac;
  const tiesOut = Math.abs(tieOut - map.projectBac) < 0.01;
  const sel = selected ? map.zones.find((z) => z.zoneCode === selected) ?? null : null;

  return (
    <div className="modelview">
      <div className="card modelview-stage">
        <div className="panel-head">
          <span className="muted small">
            Period {map.period} · {map.zones.length} zones · {map.currency}
          </span>
          <div className="seg">
            <button
              className={`btn sm ${mode === "cpi" ? "primary" : "ghost"}`}
              onClick={() => setMode("cpi")}
            >
              Cost performance
            </button>
            <button
              className={`btn sm ${mode === "exposure" ? "primary" : "ghost"}`}
              onClick={() => setMode("exposure")}
            >
              Unspent exposure
            </button>
          </div>
        </div>

        <div className="model-canvas" ref={hostRef} role="img" aria-label="Tower X massing coloured by cost performance">
          {!ready && (
            <div className="model-loading">
              <Spinner />
            </div>
          )}
        </div>

        <div className="model-legend">
          {legendFor(mode).map((l) => (
            <span key={l.label} className="legend-item" title={l.note}>
              <i style={{ background: hex(l.color) }} aria-hidden="true" />
              {l.label}
            </span>
          ))}
        </div>

        <p className="provenance-badge">
          <strong>Illustrative massing.</strong> Tower X has no published model, so this geometry is
          generated from the bill of quantities — every dimension traces to a priced BOQ line. Each
          cost zone is drawn as its own region so it stays visible; this is a schematic of where the
          money sits, not a section through the building.{" "}
          <button className="btn ghost sm" onClick={() => setShowProvenance((s) => !s)}>
            {showProvenance ? "Hide derivation" : "Show derivation"}
          </button>
        </p>

        {showProvenance && (
          <div className="grid-scroll">
            <table className="grid">
              <thead>
                <tr>
                  <th>Dimension</th>
                  <th className="num">Value</th>
                  <th>BOQ item</th>
                  <th>How it was derived</th>
                </tr>
              </thead>
              <tbody>
                {spec.dimensions.map((d) => (
                  <tr key={d.key}>
                    <td>{d.label}</td>
                    <td className="num">
                      {d.value.toLocaleString()} {d.unit}
                    </td>
                    <td className="mono">
                      {d.sourceItemRef ?? DASH}
                      {d.sourceDescription && (
                        <span className="muted small"> · {d.sourceDescription}</span>
                      )}
                    </td>
                    <td className="muted small">{d.derivation}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="modelview-side">
        <div className="card">
          <h3>Where the money still is</h3>
          <div className="kpis kpis-2">
            <div className="kpi">
              <div className="kpi-v">{millions(map.unspentInDriftingZones, map.currency)}</div>
              <div className="kpi-l">unspent in drifting zones</div>
              <div className="kpi-sub">zones below CPI 0.95</div>
            </div>
            <div className="kpi">
              <div className="kpi-v">{millions(map.projectAc, map.currency)}</div>
              <div className="kpi-l">spent to date</div>
              <div className="kpi-sub">
                of {millions(map.projectBac, map.currency)} (
                {((100 * map.projectAc) / map.projectBac).toFixed(0)}%)
              </div>
            </div>
          </div>

          <p className={`tie-out ${tiesOut ? "ok" : "bad"}`}>
            {tiesOut ? "✓" : "✕"} Zones + unmapped = {money(tieOut)} — ties out to project BAC.
            {map.unmappedCentreCount > 0 && (
              <>
                {" "}
                {map.unmappedCentreCount} centre(s) carry no location: {money(map.unmappedBac)} shown
                as unmapped, never spread across zones.
              </>
            )}
          </p>

          {undrawn.length > 0 && (
            <p className="muted small">
              No geometry drawn for {undrawn.map((z) => z.zoneCode).join(", ")} — their money is in
              the table below but not on the model.
            </p>
          )}
        </div>

        <div className="card">
          <div className="panel-head">
            <h3>Zones</h3>
          </div>
          <div className="grid-scroll">
            <table className="grid">
              <thead>
                <tr>
                  <th>Zone</th>
                  <th className="num">CPI</th>
                  <th className="num">Amber</th>
                  <th className="num">Unspent</th>
                </tr>
              </thead>
              <tbody>
                {map.zones.map((z) => (
                  <tr
                    key={z.zoneCode}
                    className={[
                      "clickable",
                      z.zoneCode === selected && "selected",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={() => setSelected(z.zoneCode === selected ? null : z.zoneCode)}
                  >
                    <td className="mono">{z.zoneCode}</td>
                    <td className="num">{z.cpi == null ? DASH : ratio(z.cpi)}</td>
                    <td className="num">
                      {z.amberCount > 0 ? (
                        <span className="pill-warn">
                          {z.amberCount}/{z.centreCount}
                        </span>
                      ) : (
                        <span className="muted">0/{z.centreCount}</span>
                      )}
                    </td>
                    <td className="num">{millions(z.unspent)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>

        {sel && (
          <div className="card">
            <h3 className="mono">{sel.zoneCode}</h3>
            {sel.cpi != null && sel.alertLevel === "GREEN" && sel.amberCount > 0 && (
              <p className="note-warn">
                This zone rolls up green (CPI {ratio(sel.cpi)}), but {sel.amberCount} of its{" "}
                {sel.centreCount} cost centres are AMBER. Aggregation is hiding them.
              </p>
            )}
            {!sel.costSufficient && (
              <p className="note-warn">
                Only {((100 * sel.ac) / sel.bac).toFixed(1)}% of this zone&apos;s budget has been
                spent — too little to judge its CPI, so no verdict is shown.
              </p>
            )}
            <div className="kpis kpis-2">
              <div className="kpi">
                <div className="kpi-v">{millions(sel.unspent, map.currency)}</div>
                <div className="kpi-l">unspent</div>
              </div>
              <div className={`kpi ${sel.cpi != null && sel.cpi < 0.95 ? "bad" : ""}`}>
                <div className="kpi-v">{sel.cpi == null ? DASH : ratio(sel.cpi)}</div>
                <div className="kpi-l">cpi</div>
              </div>
            </div>
            {sel.topRiskBccId && (
              <button
                className="btn primary"
                disabled={!centres.some((c) => c.bccId === sel.topRiskBccId)}
                onClick={() => {
                  const row = centres.find((c) => c.bccId === sel.topRiskBccId);
                  if (row) onSelectCentre?.(row);
                }}
              >
                Open worst centre: {sel.topRiskBccId}
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
