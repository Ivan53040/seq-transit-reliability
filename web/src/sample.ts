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
    modes: [
      { routeType: 3, modeName: 'Bus', observedStops: 28410, onTimeStops: 22980, onTimeRate: 0.8089, averageDelaySeconds: 102 },
      { routeType: 2, modeName: 'Train', observedStops: 9120, onTimeStops: 8390, onTimeRate: 0.92, averageDelaySeconds: 48 },
      { routeType: 4, modeName: 'Ferry', observedStops: 1640, onTimeStops: 1525, onTimeRate: 0.9299, averageDelaySeconds: 32 },
      { routeType: 0, modeName: 'Tram', observedStops: 3890, onTimeStops: 3423, onTimeRate: 0.8799, averageDelaySeconds: 61 },
    ],
    worstStops: [
      { stopId: '10795', stopName: 'Cultural Centre station, Platform 1', latitude: -27.473, longitude: 153.018, observedStops: 612, onTimeStops: 281, onTimeRate: 0.459, averageDelaySeconds: 438 },
      { stopId: '10801', stopName: 'King George Square station, 1b', latitude: -27.468, longitude: 153.024, observedStops: 540, onTimeStops: 275, onTimeRate: 0.509, averageDelaySeconds: 372 },
      { stopId: '10786', stopName: 'South Bank busway station, Platform 1', latitude: -27.481, longitude: 153.023, observedStops: 488, onTimeStops: 268, onTimeRate: 0.549, averageDelaySeconds: 324 },
      { stopId: '10790', stopName: 'Mater Hill station, Platform 1', latitude: -27.485, longitude: 153.028, observedStops: 421, onTimeStops: 244, onTimeRate: 0.58, averageDelaySeconds: 294 },
      { stopId: '600029', stopName: 'Roma Street station, Platform 5', latitude: -27.465, longitude: 153.018, observedStops: 295, onTimeStops: 194, onTimeRate: 0.658, averageDelaySeconds: 246 },
      { stopId: '10797', stopName: 'UQ Lakes station, Platform B', latitude: -27.499, longitude: 153.016, observedStops: 380, onTimeStops: 243, onTimeRate: 0.639, averageDelaySeconds: 230 },
      { stopId: '10815', stopName: 'RBWH station, Platform 1', latitude: -27.448, longitude: 153.029, observedStops: 342, onTimeStops: 236, onTimeRate: 0.69, averageDelaySeconds: 205 },
      { stopId: '10844', stopName: 'Chermside Interchange, Stop A', latitude: -27.386, longitude: 153.031, observedStops: 310, onTimeStops: 229, onTimeRate: 0.739, averageDelaySeconds: 184 },
      { stopId: '10862', stopName: 'Garden City Interchange, Stop D', latitude: -27.562, longitude: 153.081, observedStops: 288, onTimeStops: 222, onTimeRate: 0.771, averageDelaySeconds: 162 },
      { stopId: '300018', stopName: 'Southport Bus Station, Stop A', latitude: -27.969, longitude: 153.413, observedStops: 274, onTimeStops: 178, onTimeRate: 0.65, averageDelaySeconds: 228 },
      { stopId: '300412', stopName: 'Broadbeach South Station, Platform 2', latitude: -28.037, longitude: 153.431, observedStops: 315, onTimeStops: 252, onTimeRate: 0.8, averageDelaySeconds: 145 },
      { stopId: '300255', stopName: 'Surfers Paradise Cypress Ave', latitude: -27.997, longitude: 153.428, observedStops: 240, onTimeStops: 168, onTimeRate: 0.7, averageDelaySeconds: 198 },
      { stopId: '310014', stopName: 'Maroochydore Station, Platform 1', latitude: -26.656, longitude: 153.091, observedStops: 215, onTimeStops: 159, onTimeRate: 0.74, averageDelaySeconds: 176 },
      { stopId: '310188', stopName: 'Caloundra Interchange, Stop 2', latitude: -26.804, longitude: 153.133, observedStops: 190, onTimeStops: 175, onTimeRate: 0.921, averageDelaySeconds: 72 },
    ],
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
  const matched = sampleDay().routes.find((r) => r.routeId === routeId);
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
      routeShortName: matched?.routeShortName || shortName || routeId,
      routeLongName: matched?.routeLongName || `Sample Corridor ${shortName || routeId}`,
      routeType: matched?.routeType ?? 3,
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

