// Shared display formatters — one source of truth so every tab reads consistently.
// Amounts are stored in the project's reporting currency; pass that currency in as the label.

export const DASH = "—";

const INT = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

/** Whole-unit money with thousands separators + currency label. null/NaN → dash. */
export function money(value: number | null | undefined, currency = "AED"): string {
  if (value == null || !Number.isFinite(value)) return DASH;
  return `${INT.format(Math.round(value))} ${currency}`;
}

/** Compact millions (e.g. "12.4M AED"). null/NaN → dash. */
export function millions(value: number | null | undefined, currency = "AED"): string {
  if (value == null || !Number.isFinite(value)) return DASH;
  return `${(value / 1e6).toFixed(1)}M ${currency}`;
}

/** CPI/SPI-style ratio to 3 dp. null/NaN → dash. */
export function ratio(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return DASH;
  return value.toFixed(3);
}

/** Percentage where the value is already 0–100 (e.g. progress %). null/NaN → dash. */
export function pct(value: number | null | undefined, dp = 1): string {
  if (value == null || !Number.isFinite(value)) return DASH;
  return `${value.toFixed(dp)}%`;
}

/** Percentage where the value is a 0–1 fraction (e.g. precision, coverage). null/NaN → dash. */
export function pctOfFraction(value: number | null | undefined, dp = 0): string {
  if (value == null || !Number.isFinite(value)) return DASH;
  return `${(value * 100).toFixed(dp)}%`;
}
