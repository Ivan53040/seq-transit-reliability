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

/** Today's date in Brisbane (UTC+10 all year; Queensland has no daylight saving). */
export function brisbaneToday(now = new Date()): string {
  return new Date(now.getTime() + 10 * 60 * 60 * 1000).toISOString().slice(0, 10);
}
