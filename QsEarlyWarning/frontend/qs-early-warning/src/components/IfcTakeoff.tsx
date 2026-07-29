import { useCallback, useEffect, useRef, useState } from "react";
import { api, type CostMap, type TakeoffLineRequest, type TakeoffPricing } from "../api/client";
import { money, millions, DASH } from "../format";
import { hex, legendFor, type PaintMode } from "../model/costPaint";
import { buildCostLinks, TIER_CONFIDENCE, type CostLinkResult } from "../model/ifcCostLink";
import { fetchBundledIfc, loadIfc, readIfcFile } from "../model/ifcLoader";
import { measureModel, type ModelMeasurement } from "../model/ifcMeasure";
import { paintIfcByCost, unplacedLegend } from "../model/ifcPaint";
import { mapToZones, type ZoneMapResult } from "../model/ifcZoneMap";
import { createViewer, fitToBounds, type Viewer } from "../model/viewer";
import { Spinner } from "./Loading";
import * as THREE from "three";
import type * as FRAGS from "@thatopen/fragments";

/**
 * IFC Take-off — measure a real model, price it with this project's rate library, and show where
 * the cost plan would put it.
 *
 * The other 3D tab answers "where is my money in trouble" on a massing we derived from the BOQ.
 * This one starts from the opposite end: here is a real building nobody has priced — what does it
 * cost, can it even be measured, and how firmly does each element bind to a budget?
 *
 * The colours are Tower X's zone cost. The geometry is not Tower X. That gap is the point of the
 * exercise and is stated on the page rather than papered over: what travels between an arbitrary
 * model and a cost plan is the *mechanism*, and the honest measure of it is how much of the model
 * the mechanism can place, at what confidence.
 */
/** Share of a total, as a whole-number percentage. Returns a dash when there is nothing to divide. */
const pct = (n: number, total: number) => (total > 0 ? `${((100 * n) / total).toFixed(0)}%` : DASH);

/** A measured quantity, to one decimal — the precision a take-off is actually good to. */
const qty = (n: number) => n.toLocaleString(undefined, { maximumFractionDigits: 1 });

export function IfcTakeoff({ period }: { period: number }) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewerRef = useRef<Viewer | null>(null);
  /** The loaded model, held so a repaint never has to re-parse 8 MB of IFC. */
  const modelRef = useRef<FRAGS.FragmentsModel | null>(null);

  const [status, setStatus] = useState<string>("Starting viewer…");
  const [busy, setBusy] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string>("school_str.ifc");
  const [measurement, setMeasurement] = useState<ModelMeasurement | null>(null);
  const [pricing, setPricing] = useState<TakeoffPricing | null>(null);
  const [zoneMap, setZoneMap] = useState<ZoneMapResult | null>(null);
  const [links, setLinks] = useState<CostLinkResult | null>(null);
  // Tower X's cost map, used only to report which of ITS zones this model reaches — never to
  // suggest the loaded building shares that budget.
  const [costMap, setCostMap] = useState<CostMap | null>(null);
  const [showRules, setShowRules] = useState(false);

  /** Period shown in this tab. Seeded from the app selector, then scrubbable in place. */
  const [viewPeriod, setViewPeriod] = useState(period);
  useEffect(() => setViewPeriod(period), [period]);
  const [paintMode, setPaintMode] = useState<PaintMode>("cpi");

  /** Load → measure → price. One path, whether the bytes came from the bundle or a file picker. */
  const ingest = useCallback(async (bytes: Uint8Array, name: string) => {
    const viewer = viewerRef.current;
    if (!viewer) return;

    setBusy(true);
    setErr(null);
    setMeasurement(null);
    setPricing(null);
    setZoneMap(null);
    setLinks(null);
    setFileName(name);

    try {
      setStatus("Converting IFC…");
      const model = await loadIfc(viewer, bytes, (p) =>
        setStatus(`Converting IFC… ${Math.round(p * 100)}%`),
      );
      modelRef.current = model;

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

      // Zone classification, linking and paint all depend on the cost map at the SELECTED period,
      // so they live in their own effect keyed on `measurement`. Doing them here would make
      // `ingest` — a dependency of the viewer effect — depend on period state, which would tear the
      // viewer down and re-parse the 8 MB IFC on every scrub.
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
      modelRef.current = null;
      owned?.dispose();
      host.querySelectorAll("canvas").forEach((c) => c.remove());
    };
  }, [ingest]);

  // ── locate in the cost plan, then paint ──
  // Re-runs on a new model, a period scrub, or a mode switch. It never touches the viewer
  // lifecycle, so scrubbing recolours the model already on screen rather than reloading it.
  useEffect(() => {
    const viewer = viewerRef.current;
    const model = modelRef.current;
    if (!viewer || !model || !measurement) return;

    let cancelled = false;

    (async () => {
      try {
        const [cm, centres] = await Promise.all([
          api.costMap(viewPeriod).catch(() => null),
          api.costCentres(viewPeriod).catch(() => []),
        ]);
        if (cancelled) return;

        setCostMap(cm);
        const zones = cm?.zones ?? [];

        const zm = mapToZones(
          measurement.byClass,
          measurement.report.storeys,
          zones.map((z) => z.zoneCode),
        );
        if (cancelled) return;
        setZoneMap(zm);

        const linked = buildCostLinks(measurement, zm, {
          zoneCodes: zones.map((z) => z.zoneCode),
          // Both identifiers a cost centre is known by — an element naming either one was authored
          // with this cost plan in view, even when it names no zone we could paint it into.
          centreCodes: centres.flatMap((c) => [c.bccId, c.packageCode]).filter(Boolean),
        });
        if (cancelled) return;
        setLinks(linked);

        await paintIfcByCost(viewer, model, zm, zones, paintMode, {
          tierByLocalId: linked.tierByLocalId,
        });
      } catch (e) {
        if (!cancelled) setErr(String((e as Error).message ?? e));
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [measurement, viewPeriod, paintMode]);

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

          {costMap && (
            <div className="scrub">
              <label htmlFor="takeoff-period" className="muted small">Period</label>
              <input
                id="takeoff-period"
                type="range"
                min={costMap.minPeriod}
                max={costMap.maxPeriod}
                value={viewPeriod}
                onChange={(e) => setViewPeriod(Number(e.target.value))}
                aria-label={`Reporting period ${viewPeriod}`}
              />
              <span className="mono small">{viewPeriod}</span>
            </div>
          )}

          <div className="seg">
            <button
              className={`btn sm ${paintMode === "cpi" ? "primary" : "ghost"}`}
              onClick={() => setPaintMode("cpi")}
            >
              Cost performance
            </button>
            <button
              className={`btn sm ${paintMode === "exposure" ? "primary" : "ghost"}`}
              onClick={() => setPaintMode("exposure")}
            >
              Unspent exposure
            </button>
          </div>

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

        {zoneMap && (
          <div className="model-legend">
            {legendFor(paintMode).map((l) => (
              <span key={l.label} className="legend-item" title={l.note}>
                <i style={{ background: hex(l.color) }} aria-hidden="true" />
                {l.label}
              </span>
            ))}
            <span className="legend-item" title={unplacedLegend.note}>
              <i style={{ background: hex(unplacedLegend.color) }} aria-hidden="true" />
              {unplacedLegend.label}
            </span>
          </div>
        )}

        <p className="provenance-badge">
          <strong>This is not Tower X.</strong> It is a school&apos;s structural model (Autodesk
          Revit sample, IFC4) being priced with <strong>Tower X&apos;s rate library</strong> and
          coloured with <strong>Tower X&apos;s zone cost</strong>. The two buildings are unrelated —
          what is being demonstrated is that a rate library and a cost plan travel to any model you
          can measure. A colour here means &ldquo;an element of this kind maps to a zone in that
          state&rdquo;, never that this building holds that budget.{" "}
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

        {pricing && (pricing.quantityVariances.length > 0 || pricing.uncomparableQuantities.length > 0) && (
          <div className="card">
            <h3>Does the model agree with the bill?</h3>

            <p className="note-warn">
              <b>Read the mechanism, not the numbers below.</b> A school measured against Tower
              X&apos;s bill of quantities is two unrelated buildings, so the divergence here is not
              an overrun. On a project&apos;s <em>own</em> model this is the earliest warning in the
              whole system: every other signal waits for cost to be booked, this one fires while the
              concrete is still a drawing.
            </p>

            {pricing.quantityVariances.length > 0 && (
              <div className="grid-scroll">
                <table className="grid">
                  <thead>
                    <tr>
                      <th>BOQ item</th>
                      <th className="num">Model vs bill</th>
                      <th className="num">Variance</th>
                      <th className="num">At this rate</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pricing.quantityVariances.map((v) => (
                      <tr key={v.boqItemRef}>
                        <td>
                          <div className="mono">{v.boqItemRef}</div>
                          {v.boqDescription && (
                            <div className="muted small">{v.boqDescription}</div>
                          )}
                        </td>
                        <td className="num">
                          {qty(v.modelQuantity)}
                          <span className="muted"> / </span>
                          {qty(v.boqQuantity)}
                          <div className="muted small">{v.unit}</div>
                        </td>
                        <td className="num">
                          <span className={v.variance > 0 ? "pill-warn" : ""}>
                            {v.variance > 0 ? "+" : ""}
                            {qty(v.variance)}
                          </span>
                          <div className="muted small">
                            {v.variancePct > 0 ? "+" : ""}
                            {(v.variancePct * 100).toFixed(0)}%
                          </div>
                        </td>
                        <td className="num">{money(v.costImpact, pricing.currency)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {pricing.uncomparableQuantities.length > 0 && (
              <p className="muted small">
                Not compared:{" "}
                {pricing.uncomparableQuantities.map((u) => u.boqItemRef).join(", ")} — the bill
                carries no quantity for{" "}
                {pricing.uncomparableQuantities.length === 1 ? "it" : "them"}, and treating a missing
                quantity as zero would report a 100% overrun that only exists in the gap.
              </p>
            )}

            <p className="muted small">{pricing.varianceBasis}</p>
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

            {links && (
              <>
                <h4>At what confidence</h4>
                <table className="grid">
                  <tbody>
                    <tr>
                      <td>
                        <b>Direct</b>
                        <span className="muted small">
                          {" "}· the element&apos;s own properties name a zone
                        </span>
                      </td>
                      <td className="num mono">{TIER_CONFIDENCE.Direct.toFixed(2)}</td>
                      <td className="num">
                        {links.directCount}
                        <span className="muted small">
                          {" "}({pct(links.directCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                    <tr>
                      <td>
                        <b>Grouped</b>
                        <span className="muted small"> · placed by a class + storey rule</span>
                      </td>
                      <td className="num mono">{TIER_CONFIDENCE.Grouped.toFixed(2)}</td>
                      <td className="num">
                        {links.groupedCount}
                        <span className="muted small">
                          {" "}({pct(links.groupedCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                    <tr>
                      <td>
                        <b>None</b>
                        <span className="muted small"> · no rule reached it</span>
                      </td>
                      <td className="num muted">{DASH}</td>
                      <td className="num">
                        {links.noneCount}
                        <span className="muted small">
                          {" "}({pct(links.noneCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                  </tbody>
                </table>

                {links.directCount === 0 && (
                  <p className="note-warn">
                    <b>Nothing in this model links directly.</b> Not one of its{" "}
                    {links.totalElements} elements carries a cost code in its property sets, so every
                    placement above is a rule&apos;s inference about a category rather than a
                    statement by whoever authored the model. That is normal for a structural export
                    — and it is exactly the ceiling a QS should know about before trusting a
                    model-driven cost figure. Elements are drawn at reduced opacity to say so.
                  </p>
                )}

                {links.codeCarryingElements > 0 && (
                  <p className="muted small">
                    {links.codeCarryingElements} element
                    {links.codeCarryingElements === 1 ? "" : "s"} carry a recognised cost identifier
                    {links.codesFound.length > 0 && (
                      <> — <span className="mono">{links.codesFound.slice(0, 8).join(", ")}</span></>
                    )}
                    .
                  </p>
                )}
              </>
            )}

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
