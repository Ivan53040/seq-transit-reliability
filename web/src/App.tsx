import { useEffect, useMemo, useState } from 'react';
import { Link, Route, Routes, useNavigate, useSearchParams } from 'react-router-dom';
import {
  ApiError,
  brisbaneToday,
  formatMode,
  loadDay,
  type DayReport,
  type RouteOnTime,
} from './api';
import { RoutePage } from './RoutePage';
import { sampleDay } from './sample';

const ROWS_SHOWN_AT_FIRST = 25;

/** Below this many observed stops a route's rate is mostly noise, so it is listed after the others. */
export const MIN_RELIABLE_SAMPLE = 20;

type Load =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  | { state: 'ready'; report: DayReport };

export function App() {
  const [searchParams] = useSearchParams();
  const useSample = searchParams.has('sample');

  return (
    <div className="page">
      <header className="masthead">
        <Link to={`/${searchParams.toString() ? `?${searchParams.toString()}` : ''}`} className="brand-link">
          <p className="brand">
            <span className="brand-mark" aria-hidden="true" />
            SEQ Transit Reliability
          </p>
        </Link>
        <span className="tagline">South East Queensland Transit Performance</span>
      </header>

      {useSample && (
        <p className="notice" role="note">
          Sample data mode. Remove <code>?sample</code> from the address to see real live data from Translink.
        </p>
      )}

      <main>
        <Routes>
          <Route path="/" element={<BoardPage />} />
          <Route path="/routes/:routeId" element={<RoutePage />} />
        </Routes>
      </main>

      <footer className="footer">
        <p>
          On time means arriving between 1 minute early and 5 minutes late, counted only at stops the vehicle
          has already passed. Sample sizes are always presented alongside punctuality rates. Source: Translink
          real-time GTFS-RT feed and South East Queensland GTFS Timetable, licensed under CC BY 4.0.
          This is a common stop-level window, not Translink’s official performance measure.
          Cancellation counts include only cancellations explicitly reported in the feed; zero does not
          confirm that every scheduled trip ran.
        </p>
      </footer>
    </div>
  );
}

function BoardPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const today = brisbaneToday();
  const date = searchParams.get('date') || today;
  const useSample = searchParams.has('sample');

  const [load, setLoad] = useState<Load>({ state: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (useSample) {
      setLoad({ state: 'ready', report: sampleDay() });
      return;
    }

    const controller = new AbortController();
    setLoad({ state: 'loading' });

    loadDay(date, controller.signal)
      .then((report) => setLoad({ state: 'ready', report }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        const localHint = import.meta.env.DEV
          ? ' Check that the API is running: dotnet run --project src/SeqTransit.Api'
          : '';
        const message =
          error instanceof ApiError
            ? `Couldn't load the data: the API answered HTTP ${error.status} for ${error.path}.${localHint}`
            : `Couldn't reach the API.${localHint}`;
        setLoad({ state: 'error', message });
      });

    return () => controller.abort();
  }, [date, useSample, attempt]);

  const handleDateChange = (newDate: string) => {
    if (!newDate) return;
    const next = new URLSearchParams(searchParams);
    next.set('date', newDate);
    setSearchParams(next);
  };

  return (
    <>
      <section className="summary" aria-labelledby="headline">
        <Headline load={load} date={date} isToday={date === today} />
        <div className="summary-controls">
          <label className="date-field">
            <span>Service day</span>
            <input
              type="date"
              value={date}
              max={today}
              onChange={(e) => e.target.value && handleDateChange(e.target.value)}
            />
          </label>
          {load.state === 'ready' && <Freshness report={load.report} />}
        </div>
      </section>

      {load.state === 'error' && (
        <div className="message" role="alert">
          <p>{load.message}</p>
          <button type="button" onClick={() => setAttempt((n) => n + 1)}>
            Try again
          </button>
        </div>
      )}

      {load.state === 'ready' && load.report.routes.length === 0 && (
        <div className="message">
          <p>
            No stops have been recorded for this day yet. Data is loaded in batches, so new stops can take up to
            half an hour to appear.
          </p>
        </div>
      )}

      {load.state === 'ready' && load.report.weeklySummary && (
        <WeeklySummaryCard summary={load.report.weeklySummary} />
      )}

      {load.state === 'ready' && load.report.routes.length > 0 && (
        <RouteBoard report={load.report} date={date} />
      )}
    </>
  );
}

function WeeklySummaryCard({ summary }: { summary: NonNullable<DayReport['weeklySummary']> }) {
  const f = summary.inputFigures;
  return (
    <section className="ai-summary-card" aria-labelledby="ai-summary-heading">
      <div className="ai-summary-head">
        <h2 id="ai-summary-heading" className="ai-summary-title">
          Weekly reliability summary
        </h2>
        <span className="ai-badge">
          Written by AI ({summary.modelId}) from SQL figures shown
        </span>
      </div>
      <p className="ai-summary-text">{summary.summaryText}</p>
      <dl className="ai-figures-bar" aria-label="Source SQL figures">
        <div>
          <dt>Week</dt>
          <dd>{f.weekStart} to {f.weekEnd}</dd>
        </div>
        <div>
          <dt>On time</dt>
          <dd>
            {f.onTimePercent}% ({f.totalObservedStops.toLocaleString('en-AU')} stops)
            {f.changePercentagePoints !== null && (
              <span className="muted">
                {' '}({f.changePercentagePoints >= 0 ? '+' : ''}{f.changePercentagePoints} pp)
              </span>
            )}
          </dd>
        </div>
        {f.bestRoute && (
          <div>
            <dt>Best route</dt>
            <dd>{f.bestRoute.routeShortName} ({f.bestRoute.onTimePercent}%)</dd>
          </div>
        )}
        {f.worstRoute && (
          <div>
            <dt>Worst route</dt>
            <dd>{f.worstRoute.routeShortName} ({f.worstRoute.onTimePercent}%)</dd>
          </div>
        )}
        {f.worstHour && (
          <div>
            <dt>Slowest hour</dt>
            <dd>{f.worstHour.label} ({f.worstHour.onTimePercent}%)</dd>
          </div>
        )}
      </dl>
    </section>
  );
}

function Headline({ load, date, isToday }: { load: Load; date: string; isToday: boolean }) {
  if (load.state !== 'ready' || load.report.routes.length === 0) {
    return (
      <h1 id="headline" className="headline">
        {load.state === 'loading' ? 'Checking how services are running…' : 'How punctual were services?'}
      </h1>
    );
  }

  const { routes } = load.report;
  const observed = routes.reduce((sum, r) => sum + r.observedStops, 0);
  const onTime = routes.reduce((sum, r) => sum + r.onTimeStops, 0);
  const when = isToday ? 'so far today' : `on ${formatDay(date)}`;

  return (
    <div>
      <h1 id="headline" className="headline">
        On time at {percent(onTime / observed)} of stops {when}
      </h1>
      <p className="headline-detail">
        Across {routes.length.toLocaleString('en-AU')} routes and {observed.toLocaleString('en-AU')} stops served.
      </p>
    </div>
  );
}

function RouteBoard({ report, date }: { report: DayReport; date: string }) {
  const [showAll, setShowAll] = useState(false);
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  const modes = ['all', 'bus', 'train', 'ferry', 'tram'];

  // Mode and search live in the URL (?mode=train&q=66), like the date, so a shared link or a
  // reload shows the same filtered board. replace keeps typing out of the browser history.
  const query = searchParams.get('q') ?? '';
  const modeParam = searchParams.get('mode') ?? 'all';
  const mode = modes.includes(modeParam) ? modeParam : 'all';

  const updateParam = (key: string, value: string, empty: string) => {
    const next = new URLSearchParams(searchParams);
    if (value === empty) next.delete(key);
    else next.set(key, value);
    setSearchParams(next, { replace: true });
  };
  const setQuery = (value: string) => updateParam('q', value, '');
  const setMode = (value: string) => updateParam('mode', value, 'all');

  const cancelled = useMemo(
    () => new Map(report.cancellations.map((c) => [c.routeId, c.cancelledTrips])),
    [report.cancellations],
  );

  // Keep the API's worst-first order, but move routes with too few observations to the end so a
  // route seen at 2 stops can't top the board with a meaningless 0% or 50%.
  const ranked = [
    ...report.routes.filter((r) => r.observedStops >= MIN_RELIABLE_SAMPLE),
    ...report.routes.filter((r) => r.observedStops < MIN_RELIABLE_SAMPLE),
  ];

  const matches = ranked.filter((r) => {
    const modeName = formatMode(r.routeType).toLowerCase();
    if (mode !== 'all' && modeName !== mode) return false;
    const q = query.trim().toLowerCase();
    if (!q) return true;
    return (
      r.routeId.toLowerCase().includes(q) ||
      (r.routeShortName && r.routeShortName.toLowerCase().includes(q)) ||
      (r.routeLongName && r.routeLongName.toLowerCase().includes(q))
    );
  });

  const rows = showAll || query || mode !== 'all' ? matches : matches.slice(0, ROWS_SHOWN_AT_FIRST);

  const getRouteLink = (routeId: string) => {
    const params = new URLSearchParams(searchParams);
    params.set('date', date);
    // q and mode ride along so the route page's "Back to all routes" link restores the filtered board.
    return `/routes/${encodeURIComponent(routeId)}?${params.toString()}`;
  };

  return (
    <section className="board" aria-labelledby="board-title">
      <div className="board-head">
        <div>
          <h2 id="board-title">Routes, least punctual first</h2>
          <div className="mode-filters" role="group" aria-label="Filter by transport mode">
            {modes.map((m) => (
              <button
                key={m}
                type="button"
                className={`mode-btn ${mode === m ? 'active' : ''}`}
                aria-pressed={mode === m}
                onClick={() => setMode(m)}
              >
                {m === 'all' ? 'All modes' : m.charAt(0).toUpperCase() + m.slice(1)}
              </button>
            ))}
          </div>
        </div>
        <label className="search">
          <span className="visually-hidden">Find a route</span>
          <input
            type="search"
            placeholder="Find by number or name"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
      </div>

      <div className="table-responsive">
        <table>
          <thead>
            <tr>
              <th scope="col">Route</th>
              <th scope="col" className="col-rate">
                On time (Observed)
              </th>
              <th scope="col" className="num">
                Average delay
              </th>
              <th scope="col" className="num">
                Stops
              </th>
              <th scope="col" className="num">
                Feed-reported cancellations
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((route) => {
              const link = getRouteLink(route.routeId);
              return (
                <RouteRow
                  key={route.routeId}
                  route={route}
                  link={link}
                  cancelledTrips={cancelled.get(route.routeId) ?? 0}
                  onRowClick={() => navigate(link)}
                />
              );
            })}
          </tbody>
        </table>
      </div>

      {rows.length === 0 && (
        <p className="board-empty">
          No route matches “{query}” {mode !== 'all' ? `in ${mode}` : ''}.
        </p>
      )}

      {!showAll && !query && mode === 'all' && matches.length > rows.length && (
        <button type="button" className="show-all" onClick={() => setShowAll(true)}>
          Show all {matches.length} routes
        </button>
      )}
    </section>
  );
}

function RouteRow({
  route,
  link,
  cancelledTrips,
  onRowClick,
}: {
  route: RouteOnTime;
  link: string;
  cancelledTrips: number;
  onRowClick: () => void;
}) {
  const lowSample = route.observedStops < MIN_RELIABLE_SAMPLE;
  const band = lowSample ? 'muted' : route.onTimeRate >= 0.9 ? 'good' : route.onTimeRate >= 0.75 ? 'fair' : 'poor';

  // The route link in the first cell is the keyboard and screen-reader target; clicking anywhere
  // else on the row is a mouse convenience. Keeping the row a plain <tr> preserves table semantics.
  return (
    <tr className="clickable-row" onClick={onRowClick}>
      <th scope="row">
        <Link to={link} className="route-cell-link" onClick={(e) => e.stopPropagation()}>
          <div className="route-cell">
            <div className="route-header-line">
              <span className="route-badge">{route.routeShortName || route.routeId}</span>
              <span className="route-mode">{formatMode(route.routeType)}</span>
            </div>
            {route.routeLongName && (
              <span className="route-name" title={route.routeLongName}>
                {route.routeLongName}
              </span>
            )}
          </div>
        </Link>
      </th>
      <td className="col-rate">
        <div className={`rate rate-${band}`}>
          <span className="rate-track" aria-hidden="true">
            <span className="rate-fill" style={{ width: percent(route.onTimeRate) }} />
          </span>
          <div className="rate-figures">
            <span className="rate-value">{percent(route.onTimeRate)}</span>
            <span className="rate-sample-count">({route.observedStops.toLocaleString('en-AU')} stops)</span>
            {lowSample && <span className="rate-low-sample-label">Low sample</span>}
          </div>
        </div>
      </td>
      <td className="num">{formatDelay(route.averageDelaySeconds)}</td>
      <td className="num">{route.observedStops.toLocaleString('en-AU')}</td>
      <td className="num">{cancelledTrips === 0 ? <span className="muted">0</span> : cancelledTrips}</td>
    </tr>
  );
}

function Freshness({ report }: { report: DayReport }) {
  const age = report.health.snapshotAgeSeconds;
  if (age === null) {
    return <p className="freshness freshness-stale">No data loaded yet</p>;
  }
  const minutes = Math.max(1, Math.round(age / 60));
  return report.health.status === 'ok' ? (
    <p className="freshness">Updated {minutes} min ago</p>
  ) : (
    <p className="freshness freshness-stale">Data is {minutes} min old; loading may have stopped</p>
  );
}

const percent = (ratio: number) => `${Math.round(ratio * 100)}%`;

function formatDelay(seconds: number): string {
  const minutes = seconds / 60;
  if (Math.abs(minutes) < 0.1) return 'on time';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}

function formatDay(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  });
}
