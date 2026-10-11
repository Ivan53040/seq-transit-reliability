import type { DayReport } from './api';

/**
 * Made-up numbers for working on the layout before the API has data.
 * Only used in development when the URL contains ?sample. Never shipped as real data.
 */
export function sampleDay(): DayReport {
  let seed = 7;
  const random = () => {
    seed = (seed * 16807) % 2147483647;
    return seed / 2147483647;
  };

  const routes = Array.from({ length: 36 }, (_, i) => {
    const observedStops = Math.round(200 + random() * 1800);
    const onTimeRate = Math.min(0.99, 0.55 + random() * 0.45);
    return {
      routeId: `S${100 + i * 7}`,
      observedStops,
      onTimeStops: Math.round(observedStops * onTimeRate),
      onTimeRate: Math.round(onTimeRate * 10000) / 10000,
      averageDelaySeconds: Math.round((1 - onTimeRate) * 600 + random() * 60),
    };
  }).sort((a, b) => a.onTimeRate - b.onTimeRate);

  return {
    routes,
    cancellations: routes.slice(0, 5).map((r, i) => ({ routeId: r.routeId, cancelledTrips: 5 - i })),
    health: { status: 'ok', latestSnapshot: new Date(Date.now() - 6 * 60_000).toISOString(), snapshotAgeSeconds: 360 },
  };
}
