import * as THREE from "three";
import type { GeometrySpec } from "../api/client";
import type { Viewer } from "./viewer";

/**
 * Builds an illustrative Tower X massing in which every cost zone is a distinct, visible region.
 *
 * WHAT THIS IS. Tower X has no published model. Rather than draw a plausible tower and put real
 * money underneath invented geometry, the proportions come from `GET /api/v1/model/geometry-spec`,
 * where each dimension traces to a priced BOQ line (see TowerSpecDeriver).
 *
 * WHAT THIS IS NOT. It is not a building assembly. Cost zones overlap physically in a real
 * structure — the frame passes through the slabs, risers run inside the core — so a faithful
 * assembly would bury most zones inside others and paint nothing legible. Instead each zone is
 * given its own separated region: slabs, frame, core, envelope, substructure, site. Read it as a
 * schematic of where the money sits, not as a section through the building.
 *
 * WHY PLAIN THREE.JS AND NOT FRAGMENTS. The massing is built as ordinary meshes added to the
 * That Open world's scene. Building it through `editor.createElements` does render the geometry,
 * but the items never enter the model's queryable index — `getLocalIds()` and `getBoxes()` both
 * come back empty (verified), so `setColor`, `highlight` and camera-fit-to-model all silently
 * no-op, and every colour change means regenerating the model. With meshes, a repaint is a colour
 * assignment, the camera can frame real extents, and picking is a raycast. Fragments stays for
 * what it is for: loading real IFC models, where the index comes from the file.
 * (Antonio question: can procedurally created elements be indexed so per-item colour works?)
 */

export interface GeneratedTower {
  /** Scene graph holding every zone mesh. */
  group: THREE.Group;
  /** Meshes grouped by zone code — what the colour pass mutates. */
  byZone: Map<string, THREE.Mesh[]>;
  zonesDrawn: string[];
  bounds: THREE.Box3;
  dispose: () => void;
}

/** Slab/panel thicknesses, in metres. Presentation constants, not derived quantities. */
const SLAB_T = 0.35;
const FACADE_T = 0.25;
const COLUMN = 0.9;

/** Zones drawn as a see-through skin so the zones behind them stay readable. */
const TRANSLUCENT_ZONES = new Set(["EXTERNAL-FACADE"]);

export function generateTower(viewer: Viewer, spec: GeometrySpec): GeneratedTower {
  const group = new THREE.Group();
  group.name = "tower-x-massing";

  const w = spec.footprintWidthM;
  const d = spec.footprintDepthM;
  const h = spec.floorHeightM;
  const bd = spec.basementDepthM;
  const core = spec.coreWidthM;
  const floors = spec.floorCount;
  const basements = spec.basementLevels;

  const byZone = new Map<string, THREE.Mesh[]>();
  const geometries: THREE.BufferGeometry[] = [];
  const materials: THREE.Material[] = [];

  const add = (zoneCode: string, geometry: THREE.BufferGeometry, x: number, y: number, z: number) => {
    const translucent = TRANSLUCENT_ZONES.has(zoneCode);
    const material = new THREE.MeshLambertMaterial({
      color: 0xb9c3d4,
      transparent: translucent,
      opacity: translucent ? 0.28 : 1,
      depthWrite: !translucent,
      side: THREE.DoubleSide,
    });

    const mesh = new THREE.Mesh(geometry, material);
    mesh.position.set(x, y, z);
    mesh.userData.zoneCode = zoneCode;
    group.add(mesh);

    geometries.push(geometry);
    materials.push(material);

    const list = byZone.get(zoneCode) ?? [];
    list.push(mesh);
    byZone.set(zoneCode, list);
  };

  // ── SITE-WIDE: the ground plane the whole project sits on ──────────────────
  // Kept close to the footprint on purpose. The BOQ describes a wide, low building — a ~3,580 m²
  // plate over 6 floors is 60 m across and only 22 m tall — so a generous site plane would flatten
  // the building into the ground and make the vertical zones unreadable.
  const siteHalf = w * 0.75;
  add("SITE-WIDE", new THREE.BoxGeometry(w + siteHalf, 0.2, d + siteHalf), 0, -0.1, 0);

  // ── EXTERNAL: the landscaped ring outside the footprint (roads, parking) ───
  // Four narrow kerbs, not a second slab: wide ones cover the site plane entirely and SITE-WIDE
  // — its own zone, with its own cost — disappears underneath them.
  const kerb = siteHalf * 0.3;
  const ringLen = w + siteHalf;
  const ringAt = (w + siteHalf) / 2 - kerb;
  add("EXTERNAL", new THREE.BoxGeometry(ringLen, 0.6, kerb), 0, 0.3, ringAt);
  add("EXTERNAL", new THREE.BoxGeometry(ringLen, 0.6, kerb), 0, 0.3, -ringAt);
  add("EXTERNAL", new THREE.BoxGeometry(kerb, 0.6, ringLen), ringAt, 0.3, 0);
  add("EXTERNAL", new THREE.BoxGeometry(kerb, 0.6, ringLen), -ringAt, 0.3, 0);

  // ── BASEMENT: substructure slabs below grade ───────────────────────────────
  for (let b = 1; b <= basements; b++) {
    add("BASEMENT", new THREE.BoxGeometry(w * 1.1, SLAB_T, d * 1.1), 0, -b * bd, 0);
  }

  // ── BASEMENT+EXT: the service run that leaves the basement and crosses the
  //    site. A compound zone kept whole (never split across two zones — see
  //    migration 0010), so it is drawn as one continuous element. ────────────
  add("BASEMENT+EXT", new THREE.BoxGeometry(w + siteHalf, 0.8, 2.4), 0, -bd * 0.5, d * 0.62);

  // ── STRUCTURE: perimeter frame, full height ────────────────────────────────
  const topY = floors * h;
  const colH = topY + basements * bd;
  const colY = topY / 2 - (basements * bd) / 2;
  const cols: Array<[number, number]> = [
    [w / 2 - COLUMN, d / 2 - COLUMN],
    [-(w / 2 - COLUMN), d / 2 - COLUMN],
    [w / 2 - COLUMN, -(d / 2 - COLUMN)],
    [-(w / 2 - COLUMN), -(d / 2 - COLUMN)],
    [0, d / 2 - COLUMN],
    [0, -(d / 2 - COLUMN)],
    [w / 2 - COLUMN, 0],
    [-(w / 2 - COLUMN), 0],
  ];
  for (const [x, z] of cols) {
    add("STRUCTURE", new THREE.BoxGeometry(COLUMN, colH, COLUMN), x, colY, z);
  }

  // ── ALL-RISERS: the central service core shaft, full height ────────────────
  add("ALL-RISERS", new THREE.BoxGeometry(core, colH, core), 0, colY, 0);

  // ── FLOORS-ALL: one slab per floor ─────────────────────────────────────────
  for (let f = 1; f <= floors; f++) {
    add("FLOORS-ALL", new THREE.BoxGeometry(w, SLAB_T, d), 0, f * h, 0);
  }

  // ── FLOORS-B2-RF: interior fit-out band, basement-2 through roof. Drawn as an
  //    inner ring inset from the slab edge so it stays visible above it. ─────
  for (let f = 1; f <= floors; f++) {
    add("FLOORS-B2-RF", new THREE.BoxGeometry(w * 0.72, h * 0.22, d * 0.72), 0, f * h + h * 0.3, 0);
  }

  // ── KITCHEN-FLOOR: a single-floor scope, on the top occupied floor ─────────
  add(
    "KITCHEN-FLOOR",
    new THREE.BoxGeometry(w * 0.3, h * 0.5, d * 0.3),
    w * 0.26,
    floors * h + h * 0.45,
    d * 0.26,
  );

  // ── EXTERNAL-FACADE: envelope panels on all four elevations ────────────────
  const faceH = topY - h * 0.5;
  const faceY = topY / 2 + h * 0.25;
  add("EXTERNAL-FACADE", new THREE.BoxGeometry(w, faceH, FACADE_T), 0, faceY, d / 2);
  add("EXTERNAL-FACADE", new THREE.BoxGeometry(w, faceH, FACADE_T), 0, faceY, -d / 2);
  add("EXTERNAL-FACADE", new THREE.BoxGeometry(FACADE_T, faceH, d), w / 2, faceY, 0);
  add("EXTERNAL-FACADE", new THREE.BoxGeometry(FACADE_T, faceH, d), -w / 2, faceY, 0);

  viewer.world.scene.three.add(group);

  return {
    group,
    byZone,
    zonesDrawn: [...byZone.keys()],
    bounds: new THREE.Box3().setFromObject(group),
    dispose: () => {
      group.removeFromParent();
      for (const g of geometries) g.dispose();
      for (const m of materials) m.dispose();
    },
  };
}
