// Response shapes of the ASP.NET Core API (System.Text.Json, camelCase).

export interface RouteOnTime {
  routeId: string;
  routeShortName: string;
  routeLongName: string;
  routeType: number;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
}

export function formatMode(routeType: number): string {
  switch (routeType) {
    case 0: return 'Tram';
    case 1: return 'Metro';
    case 2: return 'Train';
    case 3: return 'Bus';
    case 4: return 'Ferry';
    default: return 'Transit';
  }
}

export interface RouteCancellations {
  routeId: string;
  cancelledTrips: number;
}

export interface Health {
  status: 'ok' | 'stale';
  latestSnapshot: string | null;
  snapshotAgeSeconds: number | null;
}

export interface DayReport {
  routes: RouteOnTime[];
  cancellations: RouteCancellations[];
  health: Health;
}

export class ApiError extends Error {
  constructor(
    public readonly path: string,
    public readonly status: number,
  ) {
    super(`${path} answered HTTP ${status}`);
  }
}

async function get<T>(path: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal, headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new ApiError(path, response.status);
  }
  return (await response.json()) as T;
}

export async function loadDay(date: string, signal: AbortSignal): Promise<DayReport> {
  const query = `?date=${encodeURIComponent(date)}`;
  const [routes, cancellations, health] = await Promise.all([
    get<RouteOnTime[]>(`/api/routes/on-time${query}`, signal),
    get<RouteCancellations[]>(`/api/cancellations${query}`, signal),
    get<Health>('/api/health', signal),
  ]);
  return { routes, cancellations, health };
}

export interface RouteSummary {
  routeId: string;
  routeShortName: string;
  routeLongName: string;
  routeType: number;
  fromDate: string;
  toDate: string;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
  medianDelaySeconds: number;
  p90DelaySeconds: number;
  cancelledTrips: number;
}

export interface DailyOnTime {
  serviceDate: string;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
}

export interface RouteHourPerformance {
  hour: number;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
}

export interface RouteStopPerformance {
  stopId: string;
  stopName: string;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
}

export interface RouteDetailReport {
  summary: RouteSummary;
  daily: DailyOnTime[];
  hours: RouteHourPerformance[];
  stops: RouteStopPerformance[];
  health: Health;
}

export async function loadRouteDetail(
  routeId: string,
  date: string,
  days = 7,
  signal?: AbortSignal,
): Promise<RouteDetailReport> {
  const abortSignal = signal ?? new AbortController().signal;
  const [summary, daily, hours, stops, health] = await Promise.all([
    get<RouteSummary>(`/api/routes/${encodeURIComponent(routeId)}?days=${days}`, abortSignal),
    get<DailyOnTime[]>(`/api/routes/${encodeURIComponent(routeId)}/daily?days=${days}`, abortSignal),
    get<RouteHourPerformance[]>(
      `/api/routes/${encodeURIComponent(routeId)}/hours?date=${encodeURIComponent(date)}`,
      abortSignal,
    ),
    get<RouteStopPerformance[]>(
      `/api/routes/${encodeURIComponent(routeId)}/stops?date=${encodeURIComponent(date)}`,
      abortSignal,
    ),
    get<Health>('/api/health', abortSignal),
  ]);
  return { summary, daily, hours, stops, health };
}

/** Today's date in Brisbane (UTC+10 all year; Queensland has no daylight saving). */
export function brisbaneToday(now = new Date()): string {
  return new Date(now.getTime() + 10 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

