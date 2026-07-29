import type * as FRAGS from "@thatopen/fragments";

/**
 * Measures quantities off a loaded IFC.
 *
 * <b>Why this does not use BaseQuantities.</b> The textbook take-off reads `IfcElementQuantity`
 * (`IfcQuantityVolume` / `IfcQuantityArea`). On the sample model shipped with this app — a genuine
 * Autodesk Revit 2024 → IFC4 export — there are **zero** of them. A take-off written the correct
 * way returns nothing. The numbers do exist, but only inside Revit's own parameter groups, and on
 * that file those groups are **in Spanish** (`Dimensiones` → `Volumen`, `Area`, `Longitud`).
 *
 * So quantities are read from property sets through a synonym table. This is not defensive
 * over-engineering; it is what makes the difference between a take-off that works on real
 * exporter output and one that only works on curated test files. The report says which route
 * produced the numbers so nobody mistakes a pset scrape for a certified quantity.
 */

/** Property names that carry a volume, across exporters and locales. */
const VOLUME_KEYS = [
  "volumen", "volume", "netvolume", "grossvolume", "net volume", "gross volume",
  "volumen neto", "volumen bruto",
];

/** Property names that carry an area. */
const AREA_KEYS = [
  "area", "área", "netarea", "grossarea", "net area", "gross area",
  "netsidearea", "área neta", "area neta",
];

/**
 * The names IFC's own BaseQuantities use. If a model yields quantities but none of these, the
 * numbers came from an exporter's parameter group rather than the standard — worth saying, because
 * it is exactly what makes a take-off exporter-specific.
 */
const STANDARD_KEYS = ["netvolume", "grossvolume", "netarea", "grossarea", "netsidearea"];

export interface ClassMeasurement {
  ifcClass: string;
  /**
   * Element counts per building storey, "(none)" for elements in no storey. Carried per class so a
   * downstream rule can place a slab by the level it sits on — testing the storey against the whole
   * class instead would send every slab wherever the first matching level pointed.
   *
   * Derived from `idsByStorey` — the two can never disagree.
   */
  byStorey: Record<string, number>;
  /**
   * The localIds behind each storey's count.
   *
   * The counts alone are enough to report a take-off, but not to *show* one: colouring an element
   * needs the id the Fragments model knows it by. Carrying the ids alongside the counts is what
   * lets a zone rule's verdict travel back to the geometry it was decided on.
   */
  idsByStorey: Record<string, number[]>;
  /** Elements of this class in the model. */
  elementCount: number;
  /** Elements that yielded a volume. */
  volumeCount: number;
  volume: number;
  /** Elements that yielded an area. */
  areaCount: number;
  area: number;
}

export interface MeasurabilityReport {
  totalElements: number;
  /** Elements that yielded at least one usable quantity. */
  measuredElements: number;
  /** Elements carrying no storey — they exist, but not anywhere in particular. */
  unplacedElements: number;
  /** True when the model carries no IfcElementQuantity at all. */
  baseQuantitiesEmpty: boolean;
  /** Property names actually found, so the UI can show what it keyed on. */
  quantityKeysSeen: string[];
  storeys: string[];
}

export interface ModelMeasurement {
  byClass: ClassMeasurement[];
  report: MeasurabilityReport;
  /**
   * localId → the distinct text values in that element's property sets.
   *
   * Raw material for `ifcCostLink`: if the exporter wrote a cost code anywhere into the model, it
   * is in here, and an element that carries its own code does not need a rule to guess for it.
   */
  psetTextByLocalId: Map<number, string[]>;
}

/** Classes worth measuring for a cost take-off — structural and architectural bulk. */
const MEASURED_CLASSES = [
  "IFCSLAB", "IFCCOLUMN", "IFCBEAM", "IFCWALL", "IFCWALLSTANDARDCASE", "IFCMEMBER",
  "IFCPLATE", "IFCFOOTING", "IFCPILE", "IFCCOVERING", "IFCCURTAINWALL", "IFCREINFORCINGBAR",
  "IFCSTAIR", "IFCRAMP", "IFCROOF",
];

export async function measureModel(model: FRAGS.FragmentsModel): Promise<ModelMeasurement> {
  // Anchored patterns so IFCWALL does not also sweep in IFCWALLSTANDARDCASE twice.
  const byCategory = await model.getItemsOfCategories(
    MEASURED_CLASSES.map((c) => new RegExp(`^${c}$`, "i")),
  );

  const storeyOf = await storeyIndex(model);

  const byClass: ClassMeasurement[] = [];
  const keysSeen = new Set<string>();
  const measuredIds = new Set<number>();
  const psetTextByLocalId = new Map<number, string[]>();
  let totalElements = 0;
  let measuredElements = 0;

  for (const [category, ids] of Object.entries(byCategory)) {
    const ifcClass = category.toUpperCase();
    const localIds = ids ?? [];

    if (localIds.length === 0) continue;
    totalElements += localIds.length;
    for (const id of localIds) measuredIds.add(id);

    const measurement: ClassMeasurement = {
      ifcClass, elementCount: localIds.length, byStorey: {}, idsByStorey: {},
      volumeCount: 0, volume: 0, areaCount: 0, area: 0,
    };
    for (const id of localIds) {
      const storey = storeyOf.get(id) ?? "(none)";
      (measurement.idsByStorey[storey] ??= []).push(id);
    }
    // Counts are derived, never accumulated separately — a count that could drift from the ids
    // behind it would let the tables and the painted model tell different stories.
    for (const [storey, ids] of Object.entries(measurement.idsByStorey)) {
      measurement.byStorey[storey] = ids.length;
    }

    // Pull the property sets in one call per class rather than per element.
    const data = await model.getItemsData(localIds, {
      attributesDefault: false,
      attributes: ["Name", "NominalValue"],
      relations: {
        IsDefinedBy: { attributes: true, relations: true },
        DefinesOccurrence: { attributes: false, relations: false },
      },
    });

    for (const [index, item] of data.entries()) {
      // `getItemsData` preserves the order it was asked in; `_localId` is authoritative when the
      // model carries it, and the positional fallback keeps the mapping honest when it does not.
      const localId = (item as { _localId?: { value?: number } })._localId?.value ?? localIds[index];
      if (typeof localId === "number") {
        const text = psetText(item);
        if (text.length > 0) psetTextByLocalId.set(localId, text);
      }

      const props = flattenPsets(item);
      for (const key of Object.keys(props)) keysSeen.add(key);

      const volume = pick(props, VOLUME_KEYS);
      const area = pick(props, AREA_KEYS);

      if (volume !== null && volume > 0) {
        measurement.volume += volume;
        measurement.volumeCount++;
      }
      if (area !== null && area > 0) {
        measurement.area += area;
        measurement.areaCount++;
      }
      if ((volume !== null && volume > 0) || (area !== null && area > 0)) measuredElements++;
    }

    byClass.push(measurement);
  }

  byClass.sort((a, b) => b.elementCount - a.elementCount);

  const unplacedElements = [...measuredIds].filter((id) => !storeyOf.has(id)).length;

  const quantityKeys = [...keysSeen]
    .filter((k) => matches(k, VOLUME_KEYS) || matches(k, AREA_KEYS))
    .sort();

  return {
    byClass,
    psetTextByLocalId,
    report: {
      totalElements,
      measuredElements,
      unplacedElements,
      // No standard quantity name anywhere means the take-off is riding on exporter parameters.
      baseQuantitiesEmpty: quantityKeys.every((k) => !STANDARD_KEYS.includes(k)),
      quantityKeysSeen: quantityKeys,
      storeys: await storeysOf(model),
    },
  };
}

/**
 * localId → storey name, from IfcRelContainedInSpatialStructure.
 *
 * Elements missing from this map belong to no storey at all — 69 of the sample model's do, which is
 * itself worth reporting: they exist and cost money, but a QS cannot say where they are.
 */
async function storeyIndex(model: FRAGS.FragmentsModel): Promise<Map<number, string>> {
  const out = new Map<number, string>();
  try {
    const byCategory = await model.getItemsOfCategories([/^IFCBUILDINGSTOREY$/i]);
    const storeyIds = Object.values(byCategory).flat();
    if (storeyIds.length === 0) return out;

    const data = await model.getItemsData(storeyIds, {
      relations: { ContainsElements: { attributes: false, relations: true } },
    });
    for (const storey of data) {
      const name = storey.Name && "value" in storey.Name ? String(storey.Name.value) : "(unnamed)";
      const contained = (storey.ContainsElements as FRAGS.ItemData[] | undefined) ?? [];
      for (const item of contained) {
        const id = (item as { _localId?: { value?: number } })._localId?.value;
        if (typeof id === "number") out.set(id, name);
      }
    }
  } catch {
    /* storey containment unavailable; every element reports as unplaced */
  }
  return out;
}

/** Building storey names, in model order. */
async function storeysOf(model: FRAGS.FragmentsModel): Promise<string[]> {
  try {
    const byCategory = await model.getItemsOfCategories([/^IFCBUILDINGSTOREY$/i]);
    const localIds = Object.values(byCategory).flat();
    if (localIds.length === 0) return [];
    const data = await model.getItemsData(localIds);
    return data
      .map((d) => (d.Name && "value" in d.Name ? String(d.Name.value) : null))
      .filter((n): n is string => !!n);
  } catch {
    return [];
  }
}

/** Every (name, raw value) pair across an item's property sets. */
function psetEntries(item: FRAGS.ItemData): [string, string | number | boolean][] {
  const out: [string, string | number | boolean][] = [];
  const psets = (item.IsDefinedBy as FRAGS.ItemData[] | undefined) ?? [];

  for (const pset of psets) {
    const properties = (pset as { HasProperties?: FRAGS.ItemData[] }).HasProperties;
    if (!Array.isArray(properties)) continue;

    for (const prop of properties) {
      const name = prop.Name && "value" in prop.Name ? String(prop.Name.value) : null;
      const raw = prop.NominalValue && "value" in prop.NominalValue ? prop.NominalValue.value : null;
      if (!name || raw === null || raw === undefined) continue;
      out.push([name.trim().toLowerCase(), raw as string | number | boolean]);
    }
  }
  return out;
}

/** Flattens an item's property sets into a lower-cased name → number map. */
function flattenPsets(item: FRAGS.ItemData): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [name, raw] of psetEntries(item)) {
    const num = typeof raw === "number" ? raw : Number(raw);
    if (Number.isFinite(num)) out[name] = num;
  }
  return out;
}

/**
 * The distinct non-numeric property VALUES on an item, lower-cased.
 *
 * Quantities live in property values that parse as numbers; identifiers do not. Reading only the
 * numeric ones — which is all a take-off needs — throws away every code an exporter wrote into the
 * model, and a cost code in a property set is the strongest link an element can have to a budget.
 * So the text is kept alongside the numbers, and `ifcCostLink` decides which of it means anything.
 *
 * Values, not names: nobody knows what an exporter called the field, but the code itself is the
 * code wherever it was put.
 */
function psetText(item: FRAGS.ItemData): string[] {
  const out = new Set<string>();
  for (const [, raw] of psetEntries(item)) {
    if (typeof raw === "number") continue;
    const text = String(raw).trim().toLowerCase();
    // Numeric-looking strings are quantities that happened to be written as text, not identifiers.
    if (text.length > 0 && !Number.isFinite(Number(text))) out.add(text);
  }
  return [...out];
}

function matches(key: string, keys: string[]): boolean {
  return keys.includes(key);
}

/** First matching synonym, or null when the element carries none of them. */
function pick(props: Record<string, number>, keys: string[]): number | null {
  for (const k of keys) {
    const v = props[k];
    if (typeof v === "number" && Number.isFinite(v)) return v;
  }
  return null;
}
