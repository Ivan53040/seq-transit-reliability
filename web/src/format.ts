// Shared display helpers so every view formats figures and picks colours the same way.

export type RateBand = 'good' | 'fair' | 'poor';

/** On-time thresholds used for every colour: 90%+ good, 75–89% fair, below 75% poor. */
export const GOOD_RATE = 0.9;
export const FAIR_RATE = 0.75;

export function rateBand(onTimeRate: number): RateBand {
  if (onTimeRate >= GOOD_RATE) return 'good';
  if (onTimeRate >= FAIR_RATE) return 'fair';
  return 'poor';
}

export const percent = (ratio: number) => `${Math.round(ratio * 100)}%`;

export function formatDelay(seconds: number): string {
  const minutes = seconds / 60;
  if (Math.abs(minutes) < 0.1) return 'on time';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}

export function formatOptionalDelay(seconds: number | null): string {
  return seconds === null ? '—' : formatDelay(seconds);
}

export function formatShortDate(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', { day: 'numeric', month: 'short' });
}

export function formatDay(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  });
}

/** Hours 24 and later are trips past midnight on the same service day (GTFS style). */
export function formatHour(hour: number): string {
  const clock = hour % 24;
  const label = clock === 0 ? '12 am' : clock < 12 ? `${clock} am` : clock === 12 ? '12 pm' : `${clock - 12} pm`;
  return hour >= 24 ? `${label} (after midnight)` : label;
}

/** Sets the browser tab title; pass null for the home page. */
export function pageTitle(prefix: string | null): string {
  return prefix ? `${prefix} | SEQ Transit Reliability` : 'SEQ Transit Reliability';
}
