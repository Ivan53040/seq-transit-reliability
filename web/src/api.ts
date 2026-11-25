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
    case -1: return 'Unknown';
    default: return 'Transit';
  }
}

export function modeSlug(routeType: number): string {
  switch (routeType) {
    case 0: return 'tram';
    case 1: return 'metro';
    case 2: return 'train';
    case 3: return 'bus';
    case 4: return 'ferry';
    default: return 'other';
  }
}

export interface ModeReliability {
  routeType: number;
  modeName: string;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
}

export interface WorstStopPerformance {
  stopId: string;
  stopName: string;
  latitude: number | null;
  longitude: number | null;
  observedStops: number;
  onTimeStops: number;
  onTimeRate: number;
  averageDelaySeconds: number;
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

export interface WeeklyRouteHighlight {
  routeId: string;
  routeShortName: string;
  routeLongName: string;
  observedStops: number;
  onTimePercent: number;
  averageDelayMinutes: number;
}

export interface WeeklyHourHighlight {
  hour: number;
  label: string;
  observedStops: number;
  onTimePercent: number;
  averageDelayMinutes: number;
}

export interface WeeklyFigures {
  weekStart: string;
  weekEnd: string;
  totalObservedStops: number;
  totalRoutes: number;
  onTimePercent: number;
  previousWeekOnTimePercent: number | null;
  changePercentagePoints: number | null;
  bestRoute: WeeklyRouteHighlight | null;
  worstRoute: WeeklyRouteHighlight | null;
  worstHour: WeeklyHourHighlight | null;
  totalCancellations: number;
}

export interface WeeklySummary {
  weekStart: string;
  summaryText: string;
  inputFigures: WeeklyFigures;
  modelId: string;
  createdAt: string;
}

export interface DayReport {
  routes: RouteOnTime[];
  modes: ModeReliability[];
  worstStops: WorstStopPerformance[];
  cancellations: RouteCancellations[];
  health: Health;
  weeklySummary: WeeklySummary | null;
}

export class ApiError extends Error {
  constructor(
    public readonly path: string,
    public readonly status: number,
  ) {
    super(`${path} answered HTTP ${status}`);
  }
}

const TRANSIENT_WAKE_STATUSES = new Set([502, 503, 504]);

function waitWithAbort(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      window.clearTimeout(timer);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

async function fetchWithWakeRetry(path: string, signal: AbortSignal): Promise<Response> {
  // Aurora Serverless v2 auto-pauses to 0 ACU when idle and can take ~8-12s to resume.
  // Retry transient 502/503/504 responses silently so visitors never see a cold-wake error screen.
  const delays = [2500, 4500, 7000];
  let response = await fetch(path, { signal, headers: { Accept: 'application/json' } });
  for (const delayMs of delays) {
    if (!TRANSIENT_WAKE_STATUSES.has(response.status)) {
      return response;
    }
    await waitWithAbort(delayMs, signal);
    response = await fetch(path, { signal, headers: { Accept: 'application/json' } });
  }
  return response;
}

async function get<T>(path: string, signal: AbortSignal): Promise<T> {
  const response = await fetchWithWakeRetry(path, signal);
  if (!response.ok) {
    throw new ApiError(path, response.status);
  }
  return (await response.json()) as T;
}

async function getOptional<T>(path: string, signal: AbortSignal): Promise<T | null> {
  try {
    const response = await fetchWithWakeRetry(path, signal);
    if (response.status === 404) return null;
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

export async function loadDay(date: string, signal: AbortSignal): Promise<DayReport> {
  const query = `?date=${encodeURIComponent(date)}`;
  const [routes, modes, worstStops, cancellations, health, weeklySummary] = await Promise.all([
    get<RouteOnTime[]>(`/api/routes/on-time${query}`, signal),
    getOptional<ModeReliability[]>(`/api/modes/on-time${query}`, signal),
    getOptional<WorstStopPerformance[]>(`/api/stops/worst${query}&limit=80&minObservations=20`, signal),
    get<RouteCancellations[]>(`/api/cancellations${query}`, signal),
    get<Health>('/api/health', signal),
    getOptional<WeeklySummary>('/api/summaries/latest', signal),
  ]);
  return { routes, modes: modes ?? [], worstStops: worstStops ?? [], cancellations, health, weeklySummary };
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
  /** Null when no stops were observed in the period. */
  onTimeRate: number | null;
  averageDelaySeconds: number | null;
  medianDelaySeconds: number | null;
  p90DelaySeconds: number | null;
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
  // Every part of the page is anchored on the same service date, so a shared link always shows the same view.
  const period = `days=${days}&date=${encodeURIComponent(date)}`;
  const [summary, daily, hours, stops, health] = await Promise.all([
    get<RouteSummary>(`/api/routes/${encodeURIComponent(routeId)}?${period}`, abortSignal),
    get<DailyOnTime[]>(`/api/routes/${encodeURIComponent(routeId)}/daily?${period}`, abortSignal),
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

