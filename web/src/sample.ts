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
      routeShortName: `${100 + i * 7}`,
      routeLongName: `Sample Corridor ${100 + i * 7}`,
      routeType: i % 4 === 0 ? 2 : i % 5 === 0 ? 0 : 3,
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
    weeklySummary: {
      weekStart: '2026-11-16',
      summaryText:
        'Between 16 and 22 November 2026, 84.2% of 46,109 observed stops across 36 routes ran on time, down 1.8 percentage points from 86.0% the previous week. Route 196 led reliability at 96.5% on time while Route 100 lagged at 61.2%, and 5 pm was the slowest hour at 72.3% on time.',
      inputFigures: {
        weekStart: '2026-11-16',
        weekEnd: '2026-11-22',
        totalObservedStops: 46109,
        totalRoutes: 36,
        onTimePercent: 84.2,
        previousWeekOnTimePercent: 86.0,
        changePercentagePoints: -1.8,
        bestRoute: {
          routeId: 'S196',
          routeShortName: '196',
          routeLongName: 'Sample Corridor 196',
          observedStops: 820,
          onTimePercent: 96.5,
          averageDelayMinutes: 0.4,
        },
        worstRoute: {
          routeId: 'S100',
          routeShortName: '100',
          routeLongName: 'Sample Corridor 100',
          observedStops: 340,
          onTimePercent: 61.2,
          averageDelayMinutes: 4.7,
        },
        worstHour: {
          hour: 17,
          label: '5 pm',
          observedStops: 5210,
          onTimePercent: 72.3,
          averageDelayMinutes: 3.1,
        },
        totalCancellations: 15,
      },
      modelId: 'amazon.nova-micro-v1:0',
      createdAt: new Date(Date.now() - 3600_000).toISOString(),
    },
  };
}

export function sampleRouteDetail(routeId: string, date: string): import('./api').RouteDetailReport {
  const shortName = routeId.replace(/^S/, '');
  const baseDate = new Date(`${date}T00:00:00`);

  const daily = Array.from({ length: 7 }, (_, i) => {
    const day = new Date(baseDate.getTime() - (6 - i) * 86400000).toISOString().slice(0, 10);
    const rate = 0.78 + ((i * 17) % 15) * 0.01;
    const obs = 420 + ((i * 31) % 100);
    return {
      serviceDate: day,
      observedStops: obs,
      onTimeStops: Math.round(obs * rate),
      onTimeRate: Math.round(rate * 1000) / 1000,
      averageDelaySeconds: Math.round((1 - rate) * 600),
    };
  });

  const hours = Array.from({ length: 19 }, (_, i) => {
    const hour = i + 5; // 5 am to 11 pm
    const isPeak = (hour >= 7 && hour <= 9) || (hour >= 16 && hour <= 18);
    const rate = isPeak ? 0.71 + (hour % 3) * 0.03 : 0.89 + (hour % 4) * 0.02;
    const obs = isPeak ? 180 + hour * 8 : 70 + hour * 3;
    return {
      hour,
      observedStops: obs,
      onTimeStops: Math.round(obs * rate),
      onTimeRate: Math.round(rate * 1000) / 1000,
      averageDelaySeconds: isPeak ? 220 + (hour % 5) * 20 : 65 + (hour % 4) * 15,
    };
  });

  const stopNames = [
    'Cultural Centre station, Platform 1',
    'South Bank busway station, Platform 1',
    'Mater Hill station, Platform 1',
    'PA Hospital station, Platform 1',
    'Boggo Road station, Platform 1',
    'UQ Lakes station',
    'King George Square station, 1b',
    'Roma Street busway, Platform 1',
    'Normanby busway station',
    'Herston busway station',
    'RBWH station, Platform 1',
  ];

  const stops = stopNames.map((name, i) => {
    const rate = Math.min(0.96, Math.max(0.62, 0.94 - i * 0.03 + (i % 2 === 0 ? 0.05 : -0.04)));
    const obs = 240 + i * 15;
    return {
      stopId: `STOP-${1000 + i}`,
      stopName: name,
      observedStops: obs,
      onTimeStops: Math.round(obs * rate),
      onTimeRate: Math.round(rate * 1000) / 1000,
      averageDelaySeconds: Math.round((1 - rate) * 550),
    };
  }).sort((a, b) => a.onTimeRate - b.onTimeRate);

  const totalObs = daily.reduce((sum, d) => sum + d.observedStops, 0);
  const totalOnTime = daily.reduce((sum, d) => sum + d.onTimeStops, 0);

  return {
    summary: {
      routeId,
      routeShortName: shortName || routeId,
      routeLongName: `Sample Corridor ${shortName || routeId}`,
      routeType: 3,
      fromDate: daily[0].serviceDate,
      toDate: daily[6].serviceDate,
      observedStops: totalObs,
      onTimeStops: totalOnTime,
      onTimeRate: Math.round((totalOnTime / totalObs) * 1000) / 1000,
      averageDelaySeconds: 112.5,
      medianDelaySeconds: 42.0,
      p90DelaySeconds: 215.0,
      cancelledTrips: 2,
    },
    daily,
    hours,
    stops,
    health: { status: 'ok', latestSnapshot: new Date().toISOString(), snapshotAgeSeconds: 120 },
  };
}

