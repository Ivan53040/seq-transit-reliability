import { useEffect, useMemo, useState } from 'react';
import { ApiError, brisbaneToday, loadDay, type DayReport, type RouteOnTime } from './api';
import { sampleDay } from './sample';

const ROWS_SHOWN_AT_FIRST = 25;
const useSample = import.meta.env.DEV && new URLSearchParams(window.location.search).has('sample');

type Load =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  | { state: 'ready'; report: DayReport };

export function App() {
  const today = brisbaneToday();
  const [date, setDate] = useState(today);
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
  }, [date, attempt]);

  return (
    <div className="page">
      <header className="masthead">
        <p className="brand">
          <span className="brand-mark" aria-hidden="true" />
          SEQ Transit Reliability
        </p>
        {load.state === 'ready' && <Freshness report={load.report} />}
      </header>

      {useSample && (
        <p className="notice" role="note">
          Sample data. Remove <code>?sample</code> from the address to see real numbers from the API.
        </p>
      )}

      <main>
        <section className="summary" aria-labelledby="headline">
          <Headline load={load} date={date} isToday={date === today} />
          <label className="date-field">
            <span>Service day</span>
            <input type="date" value={date} max={today} onChange={(e) => e.target.value && setDate(e.target.value)} />
          </label>
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

        {load.state === 'ready' && load.report.routes.length > 0 && <RouteBoard report={load.report} />}
      </main>

      <footer className="footer">
        <p>
          On time means arriving between 1 minute early and 5 minutes late, counted only at stops the vehicle has
          already passed. Source: Translink real-time data (GTFS-RT), licensed under CC BY 4.0.
        </p>
      </footer>
    </div>
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

function RouteBoard({ report }: { report: DayReport }) {
  const [query, setQuery] = useState('');
  const [showAll, setShowAll] = useState(false);

  const cancelled = useMemo(
    () => new Map(report.cancellations.map((c) => [c.routeId, c.cancelledTrips])),
    [report.cancellations],
  );
  const matches = report.routes.filter((r) => r.routeId.toLowerCase().includes(query.trim().toLowerCase()));
  const rows = showAll || query ? matches : matches.slice(0, ROWS_SHOWN_AT_FIRST);

  return (
    <section className="board" aria-labelledby="board-title">
      <div className="board-head">
        <h2 id="board-title">Routes, least punctual first</h2>
        <label className="search">
          <span className="visually-hidden">Find a route</span>
          <input type="search" placeholder="Find a route" value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
      </div>

      <table>
        <thead>
          <tr>
            <th scope="col">Route</th>
            <th scope="col" className="col-rate">On time</th>
            <th scope="col" className="num">Average delay</th>
            <th scope="col" className="num">Stops</th>
            <th scope="col" className="num">Cancelled trips</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((route) => (
            <RouteRow key={route.routeId} route={route} cancelledTrips={cancelled.get(route.routeId) ?? 0} />
          ))}
        </tbody>
      </table>

      {rows.length === 0 && <p className="board-empty">No route matches “{query}”.</p>}

      {!showAll && !query && matches.length > rows.length && (
        <button type="button" className="show-all" onClick={() => setShowAll(true)}>
          Show all {matches.length} routes
        </button>
      )}
    </section>
  );
}

function RouteRow({ route, cancelledTrips }: { route: RouteOnTime; cancelledTrips: number }) {
  const band = route.onTimeRate >= 0.9 ? 'good' : route.onTimeRate >= 0.75 ? 'fair' : 'poor';
  return (
    <tr>
      <th scope="row">
        <span className="route-badge">{route.routeId}</span>
      </th>
      <td className="col-rate">
        <div className={`rate rate-${band}`}>
          <span className="rate-track" aria-hidden="true">
            <span className="rate-fill" style={{ width: percent(route.onTimeRate) }} />
          </span>
          <span className="rate-value">{percent(route.onTimeRate)}</span>
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
  if (Math.abs(minutes) < 0.1) return 'on schedule';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}

function formatDay(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', { weekday: 'long', day: 'numeric', month: 'long' });
}
