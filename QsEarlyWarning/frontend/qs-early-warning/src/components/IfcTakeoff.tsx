import { useCallback, useEffect, useRef, useState } from "react";
import { api, type CostMap, type TakeoffLineRequest, type TakeoffPricing } from "../api/client";
import { money, millions, DASH } from "../format";
import { fetchBundledIfc, loadIfc, readIfcFile } from "../model/ifcLoader";
import { measureModel, type ModelMeasurement } from "../model/ifcMeasure";
import { mapToZones, type ZoneMapResult } from "../model/ifcZoneMap";
import { createViewer, fitToBounds, type Viewer } from "../model/viewer";
import { Spinner } from "./Loading";
import * as THREE from "three";

/**
 * IFC Take-off — measure a real model, price it with this project's rate library.
 *
 * The other 3D tab answers "where is my money in trouble" for a project we already run. This one
 * answers the other half of the job: here is a building nobody has priced — what does it cost, and
 * can it even be measured?
 *
 * Deliberately absent: CPI, alert levels, watchlist. The loaded model has no cost history and we
 * do not invent one for it.
 */
export function IfcTakeoff() {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewerRef = useRef<Viewer | null>(null);

  const [status, setStatus] = useState<string>("Starting viewer…");
  const [busy, setBusy] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string>("school_str.ifc");
  const [measurement, setMeasurement] = useState<ModelMeasurement | null>(null);
  const [pricing, setPricing] = useState<TakeoffPricing | null>(null);
  const [zoneMap, setZoneMap] = useState<ZoneMapResult | null>(null);
  // Tower X's cost map, used only to report which of ITS zones this model reaches — never to
  // suggest the loaded building shares that budget.
  const [costMap, setCostMap] = useState<CostMap | null>(null);
  const [showRules, setShowRules] = useState(false);

  /** Load → measure → price. One path, whether the bytes came from the bundle or a file picker. */
  const ingest = useCallback(async (bytes: Uint8Array, name: string) => {
    const viewer = viewerRef.current;
    if (!viewer) return;

    setBusy(true);
    setErr(null);
    setMeasurement(null);
    setPricing(null);
    setZoneMap(null);
    setFileName(name);

    try {
      setStatus("Converting IFC…");
      const model = await loadIfc(viewer, bytes, (p) =>
        setStatus(`Converting IFC… ${Math.round(p * 100)}%`),
      );

      const boxes = await model.getBoxes();
      if (boxes?.length) {
        const merged = new THREE.Box3();
        for (const b of boxes) merged.union(b);
        await fitToBounds(viewer, merged);
      }

      setStatus("Measuring elements…");
      const measured = await measureModel(model);
      setMeasurement(measured);

      setStatus("Pricing against the rate library…");
      const lines: TakeoffLineRequest[] = [];
      for (const c of measured.byClass) {
        lines.push({
          ifcClass: c.ifcClass,
          measure: "volume",
          quantity: c.volume,
          elementCount: c.volumeCount > 0 ? c.elementCount : 0,
          unmeasuredCount: c.volumeCount > 0 ? 0 : c.elementCount,
        });
        // Area rides alongside volume for the same elements, so it contributes no element count —
        // counting them twice would break the tie-out against the model's real element total.
        if (c.area > 0) {
          lines.push({
            ifcClass: c.ifcClass, measure: "area", quantity: c.area,
            elementCount: 0, unmeasuredCount: 0,
          });
        }
      }

      setPricing(await api.priceTakeoff(lines, measured.report.totalElements));

      // Classify against Tower X's zones and report how much of the model a rule set can place.
      // Fetched per ingest rather than cached in state: `ingest` is a dependency of the viewer
      // effect, so making it depend on cost-map state would tear the viewer down and re-load the
      // 8 MB IFC every time the map arrived.
      const cm = await api.costMap().catch(() => null);
      setCostMap(cm);
      setZoneMap(mapToZones(measured.byClass, measured.report.storeys, (cm?.zones ?? []).map((z) => z.zoneCode)));

      setStatus("");
    } catch (e) {
      setErr(String((e as Error).message ?? e));
    } finally {
      setBusy(false);
    }
  }, []);

  // ── viewer lifecycle + first load ──
  useEffect(() => {
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

        setStatus("Fetching bundled model…");
        const bytes = await fetchBundledIfc();
        if (cancelled) return;
        await ingest(bytes, "school_str.ifc");
      } catch (e) {
        if (!cancelled) {
          setErr(String((e as Error).message ?? e));
          setBusy(false);
        }
      }
    })();

    return () => {
      cancelled = true;
      viewerRef.current = null;
      owned?.dispose();
      host.querySelectorAll("canvas").forEach((c) => c.remove());
    };
  }, [ingest]);

  const onPick = async (file: File | undefined) => {
    if (!file) return;
    await ingest(await readIfcFile(file), file.name);
  };

  const report = measurement?.report;
  const coverage = report && report.totalElements > 0
    ? (100 * report.measuredElements) / report.totalElements
    : null;

  return (
    <div className="modelview">
      <div className="card modelview-stage">
        <div className="panel-head">
          <span className="muted small mono">{fileName}</span>
          <label className="btn secondary sm file-pick">
            Load another IFC
            <input
              type="file"
              accept=".ifc"
              onChange={(e) => void onPick(e.target.files?.[0])}
              disabled={busy}
            />
          </label>
        </div>

        <div className="model-canvas" ref={hostRef} role="img" aria-label="Loaded IFC model">
          {busy && (
            <div className="model-loading">
              <Spinner />
              <p className="muted small">{status}</p>
            </div>
          )}
        </div>

        <p className="provenance-badge">
          <strong>This is not Tower X.</strong> It is a school&apos;s structural model (Autodesk
          Revit sample, IFC4) being priced with <strong>Tower X&apos;s rate library</strong>. The two
          buildings are unrelated — what is being demonstrated is that a rate library travels to any
          model you can measure.{" "}
          <button className="btn ghost sm" onClick={() => setShowRules((s) => !s)}>
            {showRules ? "Hide pricing rules" : "Show pricing rules"}
          </button>
        </p>

        {showRules && pricing && (
          <>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>IFC class</th>
                    <th>Measure</th>
                    <th>BOQ item</th>
                    <th>Why this pairing</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.rulesApplied.map((r) => (
                    <tr key={`${r.ifcClass}-${r.measure}`}>
                      <td className="mono">{r.ifcClass}</td>
                      <td>{r.measure} ({r.unit})</td>
                      <td className="mono">{r.boqItemRef}</td>
                      <td className="muted small">{r.rationale}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted small">{pricing.rateBasis}</p>
          </>
        )}
      </div>

      <div className="modelview-side">
        {err && <div className="error">{err}</div>}

        {pricing && (
          <div className="card">
            <h3>Priced at Tower X&apos;s rates</h3>
            <div className="kpis kpis-2">
              <div className="kpi">
                <div className="kpi-v">{millions(pricing.pricedAmount, pricing.currency)}</div>
                <div className="kpi-l">priceable scope</div>
                <div className="kpi-sub">{money(pricing.pricedAmount, pricing.currency)}</div>
              </div>
              <div className="kpi">
                <div className="kpi-v">
                  {coverage == null ? DASH : `${coverage.toFixed(0)}%`}
                </div>
                <div className="kpi-l">measurable</div>
                <div className="kpi-sub">
                  {report?.measuredElements ?? 0} of {report?.totalElements ?? 0} elements
                </div>
              </div>
            </div>

            <p className={`tie-out ${pricing.tiesOut ? "ok" : "bad"}`}>
              {pricing.tiesOut ? "✓" : "✕"} {pricing.pricedElements} priced +{" "}
              {pricing.unpricedElements} unpriced + {pricing.unmeasuredElements} unmeasured ={" "}
              {pricing.totalElements} elements in the model.
              {!pricing.tiesOut && " Elements are unaccounted for — the priced figure understates the building."}
            </p>
          </div>
        )}

        {pricing && pricing.priced.length > 0 && (
          <div className="card">
            <h3>What could be priced</h3>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">Quantity</th>
                    <th className="num">Rate</th>
                    <th className="num">Amount</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.priced.map((p) => (
                    <tr key={`${p.ifcClass}-${p.measure}`}>
                      <td>
                        <span className="mono">{p.ifcClass}</span>
                        <span className="muted small"> · {p.boqItemRef}</span>
                      </td>
                      <td className="num">
                        {p.quantity.toLocaleString(undefined, { maximumFractionDigits: 1 })} {p.unit}
                      </td>
                      <td className="num">{p.unitRate.toFixed(2)}</td>
                      <td className="num">{money(p.amount, pricing.currency)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {pricing && pricing.unpriced.length > 0 && (
          <div className="card">
            <h3>What could not — and why</h3>
            <p className="note-warn">
              The priced figure above is only the scope below the line. This is what it excludes.
            </p>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">Quantity</th>
                    <th className="num">Elements</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.unpriced.map((u) => (
                    <tr key={`${u.ifcClass}-${u.measure}`}>
                      <td className="mono">{u.ifcClass}</td>
                      <td className="num">
                        {u.quantity > 0
                          ? `${u.quantity.toLocaleString(undefined, { maximumFractionDigits: 1 })} ${
                              u.measure === "volume" ? "m³" : "m²"
                            }`
                          : DASH}
                      </td>
                      {/* Area rides on elements already counted under their volume line, so its
                          element count is 0 by design — never a sign of missing data. */}
                      <td className="num">{u.elementCount > 0 ? u.elementCount : DASH}</td>
                      <td className="muted small">{u.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {report && (
          <div className="card">
            <h3>Can this model be measured?</h3>
            {report.baseQuantitiesEmpty && (
              <p className="note-warn">
                This model carries <strong>no standard IFC BaseQuantities</strong>. A take-off written
                the textbook way returns nothing. The quantities here were read from the exporter&apos;s
                own property sets
                {report.quantityKeysSeen.length > 0 && (
                  <> — keyed on <span className="mono">{report.quantityKeysSeen.join(", ")}</span></>
                )}
                , which makes them exporter- and language-specific.
              </p>
            )}
            <table className="grid">
              <tbody>
                <tr>
                  <td>Elements</td>
                  <td className="num">{report.totalElements}</td>
                </tr>
                <tr>
                  <td>Carrying a usable quantity</td>
                  <td className="num">
                    {report.measuredElements}
                    {coverage != null && <span className="muted small"> ({coverage.toFixed(0)}%)</span>}
                  </td>
                </tr>
                <tr>
                  <td>In no building storey</td>
                  <td className="num">
                    {report.unplacedElements > 0 ? (
                      <span className="pill-warn">{report.unplacedElements}</span>
                    ) : (
                      report.unplacedElements
                    )}
                  </td>
                </tr>
                <tr>
                  <td>Storeys</td>
                  <td className="num mono">{report.storeys.length}</td>
                </tr>
              </tbody>
            </table>
            {report.storeys.length > 0 && (
              <p className="muted small">{report.storeys.join(" · ")}</p>
            )}
          </div>
        )}

        {zoneMap && (
          <div className="card">
            <h3>Could this model be located in the cost plan?</h3>
            <div className="kpis kpis-2">
              <div className="kpi">
                <div className="kpi-v">{(zoneMap.matchRate * 100).toFixed(0)}%</div>
                <div className="kpi-l">elements placed</div>
                <div className="kpi-sub">
                  {zoneMap.matchedElements} of {zoneMap.totalElements} by class + storey
                </div>
              </div>
              <div className="kpi">
                <div className="kpi-v">{zoneMap.matched.length}</div>
                <div className="kpi-l">zones reached</div>
                <div className="kpi-sub">of {costMap?.zones.length ?? 0} in the cost plan</div>
              </div>
            </div>

            <p className="note-warn">
              This shows the <b>mechanism</b>, not a budget. The loaded model is a school and the zones
              belong to Tower X — a matched element means &ldquo;an element of this kind would map
              here&rdquo;, never that it shares that budget.
            </p>

            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Zone</th>
                    <th className="num">Elements</th>
                    <th>From</th>
                  </tr>
                </thead>
                <tbody>
                  {zoneMap.matched.map((m) => (
                    <tr key={m.zoneCode}>
                      <td className="mono">{m.zoneCode}</td>
                      <td className="num">{m.elementCount}</td>
                      <td className="muted small mono">{m.ifcClasses.join(", ")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {zoneMap.unmatched.length > 0 && (
              <p className="muted small">
                No rule placed{" "}
                {zoneMap.unmatched.map((u) => `${u.ifcClass} (${u.elementCount})`).join(", ")}.
              </p>
            )}

            {zoneMap.zonesWithNoGeometry.length > 0 && (
              <p className="muted small">
                <b>{zoneMap.zonesWithNoGeometry.length} of Tower X&apos;s zones got nothing from this
                model</b> — {zoneMap.zonesWithNoGeometry.join(", ")}. A structural model carries no MEP,
                finishes or landscaping, and a match rate that ignored that would flatter itself.
              </p>
            )}
          </div>
        )}

        {measurement && (
          <div className="card">
            <h3>Measured by class</h3>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">n</th>
                    <th className="num">Volume m³</th>
                    <th className="num">Area m²</th>
                  </tr>
                </thead>
                <tbody>
                  {measurement.byClass.map((c) => (
                    <tr key={c.ifcClass}>
                      <td className="mono">{c.ifcClass}</td>
                      <td className="num">{c.elementCount}</td>
                      <td className="num">
                        {c.volume > 0
                          ? c.volume.toLocaleString(undefined, { maximumFractionDigits: 1 })
                          : DASH}
                      </td>
                      <td className="num">
                        {c.area > 0
                          ? c.area.toLocaleString(undefined, { maximumFractionDigits: 1 })
                          : DASH}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
