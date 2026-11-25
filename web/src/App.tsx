import { useEffect, useMemo, useState } from 'react';
import { Link, Route, Routes, useNavigate, useSearchParams } from 'react-router-dom';
import {
  ApiError,
  brisbaneToday,
  formatMode,
  loadDay,
  modeSlug,
  type DayReport,
  type ModeReliability,
  type RouteOnTime,
} from './api';
import { RoutePage } from './RoutePage';
import { sampleDay } from './sample';

const ROWS_SHOWN_AT_FIRST = 25;
// Below this many stops, a route's on-time rate is too noisy to rank near the top.
const MIN_RELIABLE_SAMPLE = 20;

const CANONICAL_MODES = [
  { routeType: 3, key: 'bus', label: 'Bus' },
  { routeType: 2, key: 'train', label: 'Train' },
  { routeType: 4, key: 'ferry', label: 'Ferry' },
  { routeType: 0, key: 'tram', label: 'Tram' },
] as const;

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
          <div className="brand">
            <svg className="brand-emblem" viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
              <rect width="24" height="24" fill="currentColor" />
              <line x1="5" y1="8" x2="19" y2="8" stroke="var(--paper)" strokeWidth="2.2" />
              <line x1="5" y1="16" x2="19" y2="16" stroke="var(--paper)" strokeWidth="2.2" />
              <circle cx="9" cy="8" r="2.2" fill="var(--signal-red)" />
              <circle cx="15" cy="16" r="2.2" fill="var(--paper)" />
            </svg>
            <span className="brand-wordmark">
              <strong>SEQ TRANSIT</strong>
              <span className="brand-sub">RELIABILITY</span>
            </span>
          </div>
        </Link>
        <span className="tagline">South East Queensland · GTFS-RT Monitor</span>
      </header>

      {useSample && (
        <p className="notice" role="note">
          Sample data mode. Remove <code>?sample</code> from the address to view live Translink data.
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
          has already passed. Sample sizes are always shown alongside rates. Source: Translink real-time
          GTFS-RT feed and SEQ GTFS Timetable (CC BY 4.0). This is a stop-level window, not Translink’s
          official performance measure. Feed-reported cancellation counts reflect only cancellations published
          in the feed; zero does not confirm that every scheduled trip ran.
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

  const modes = ['all', 'bus', 'train', 'ferry', 'tram'];
  const modeParam = searchParams.get('mode') ?? 'all';
  const activeMode = modes.includes(modeParam) ? modeParam : 'all';

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

  const handleModeSelect = (nextMode: string) => {
    const next = new URLSearchParams(searchParams);
    if (nextMode === 'all' || nextMode === activeMode) next.delete('mode');
    else next.set('mode', nextMode);
    setSearchParams(next, { replace: true });
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

      {load.state === 'loading' && <BoardSkeleton />}

      {load.state === 'error' && (
        <div className="message" role="alert">
          <p>{load.message}</p>
          <button type="button" onClick={() => setAttempt((n) => n + 1)}>
            Try again
          </button>
        </div>
      )}

      {load.state === 'ready' && load.report.routes.length > 0 && (
        <ModeStrip
          modes={load.report.modes}
          activeMode={activeMode}
          onSelectMode={handleModeSelect}
        />
      )}

      {load.state === 'ready' && load.report.weeklySummary && (
        <WeeklyBrief summary={load.report.weeklySummary} />
      )}

      {load.state === 'ready' && load.report.routes.length === 0 && (
        <div className="message">
          <p>
            No stops have been recorded for this day yet. Data is loaded in batches, so new stops can take up to
            an hour to appear.
          </p>
        </div>
      )}

      {load.state === 'ready' && load.report.routes.length > 0 && (
        <RouteBoard report={load.report} date={date} mode={activeMode} onSelectMode={handleModeSelect} />
      )}
    </>
  );
}

function ModeStrip({
  modes,
  activeMode,
  onSelectMode,
}: {
  modes: ModeReliability[];
  activeMode: string;
  onSelectMode: (mode: string) => void;
}) {
  const byType = new Map(modes.map((m) => [m.routeType, m]));

  return (
    <section className="mode-strip" aria-label="Punctuality by transport mode">
      <div className="section-kicker-row">
        <span className="section-kicker">Network by mode</span>
        {activeMode !== 'all' && (
          <button type="button" className="text-reset-btn" onClick={() => onSelectMode('all')}>
            Clear filter ({activeMode})
          </button>
        )}
      </div>
      <div className="mode-grid" role="group" aria-label="Filter routes by mode">
        {CANONICAL_MODES.map(({ routeType, key, label }) => {
          const stat = byType.get(routeType);
          const hasData = stat !== undefined && stat.observedStops > 0;
          const isSelected = activeMode === key;
          const band = !hasData
            ? 'muted'
            : stat.onTimeRate >= 0.9
              ? 'good'
              : stat.onTimeRate >= 0.75
                ? 'fair'
                : 'poor';

          return (
            <button
              key={key}
              type="button"
              className={`mode-tile mode-accent-${key} ${isSelected ? 'selected' : ''}`}
              aria-pressed={isSelected}
              onClick={() => onSelectMode(key)}
            >
              <div className="mode-tile-top">
                <span className="mode-tile-name">{label}</span>
                <span className="mode-tile-dot" aria-hidden="true" />
              </div>
              <div className={`mode-tile-rate rate-${band}`}>
                {hasData ? percent(stat.onTimeRate) : '—'}
              </div>
              <div className="mode-tile-meta">
                {hasData ? (
                  <>
                    <span>{formatDelay(stat.averageDelaySeconds)}</span>
                    <span className="meta-sep" aria-hidden="true">·</span>
                    <span>{stat.observedStops.toLocaleString('en-AU')} stops</span>
                  </>
                ) : (
                  <span>No stops recorded</span>
                )}
              </div>
            </button>
          );
        })}
      </div>
    </section>
  );
}

function WeeklyBrief({ summary }: { summary: NonNullable<DayReport['weeklySummary']> }) {
  const f = summary.inputFigures;
  return (
    <section className="weekly-brief" aria-labelledby="weekly-brief-heading">
      <div className="weekly-brief-aside">
        <h2 id="weekly-brief-heading" className="section-kicker">
          Weekly brief
        </h2>
        <p className="brief-period">
          {formatShortDate(f.weekStart)} – {formatShortDate(f.weekEnd)}
        </p>
        <p className="brief-meta">
          Written by AI ({summary.modelId}) from SQL figures shown.
        </p>
      </div>
      <div className="weekly-brief-body">
        <p className="brief-text">{summary.summaryText}</p>
        <dl className="brief-figures" aria-label="Source SQL figures">
          <div>
            <dt>Week on time</dt>
            <dd>
              {f.onTimePercent}%
              <span className="muted"> ({f.totalObservedStops.toLocaleString('en-AU')} stops)</span>
              {f.changePercentagePoints !== null && (
                <span className="muted">
                  {' '}{f.changePercentagePoints >= 0 ? '+' : ''}{f.changePercentagePoints} pp
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
      </div>
    </section>
  );
}

function BoardSkeleton() {
  return (
    <div className="skeleton-wrap" aria-hidden="true">
      <div className="skeleton-modes">
        <div className="skeleton-block" />
        <div className="skeleton-block" />
        <div className="skeleton-block" />
        <div className="skeleton-block" />
      </div>
      <div className="skeleton-rows">
        {Array.from({ length: 6 }, (_, i) => (
          <div key={i} className="skeleton-row" />
        ))}
      </div>
    </div>
  );
}

function Headline({ load, date, isToday }: { load: Load; date: string; isToday: boolean }) {
  if (load.state !== 'ready' || load.report.routes.length === 0) {
    return (
      <div>
        <p className="headline-kicker">South East Queensland · Network status</p>
        <h1 id="headline" className="headline">
          {load.state === 'loading' ? 'Reading timetable performance…' : 'No stops recorded'}
        </h1>
      </div>
    );
  }

  const { routes } = load.report;
  const observed = routes.reduce((sum, r) => sum + r.observedStops, 0);
  const onTime = routes.reduce((sum, r) => sum + r.onTimeStops, 0);
  const rate = onTime / observed;
  const band = rate >= 0.9 ? 'good' : rate >= 0.75 ? 'fair' : 'poor';
  const when = isToday ? 'So far today' : formatDay(date);

  return (
    <div>
      <p className="headline-kicker">{when} · {routes.length.toLocaleString('en-AU')} active routes</p>
      <h1 id="headline" className="headline">
        <span className={`headline-figure rate-${band}`}>{percent(rate)}</span> on time
      </h1>
      <p className="headline-detail">
        Measured across {observed.toLocaleString('en-AU')} completed stop arrivals.
      </p>
    </div>
  );
}

function RouteBoard({
  report,
  date,
  mode,
  onSelectMode,
}: {
  report: DayReport;
  date: string;
  mode: string;
  onSelectMode: (mode: string) => void;
}) {
  const [showAll, setShowAll] = useState(false);
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  const modes = ['all', 'bus', 'train', 'ferry', 'tram'];
  const query = searchParams.get('q') ?? '';

  const setQuery = (value: string) => {
    const next = new URLSearchParams(searchParams);
    if (value === '') next.delete('q');
    else next.set('q', value);
    setSearchParams(next, { replace: true });
  };

  const cancelled = useMemo(
    () => new Map(report.cancellations.map((c) => [c.routeId, c.cancelledTrips])),
    [report.cancellations],
  );

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
    return `/routes/${encodeURIComponent(routeId)}?${params.toString()}`;
  };

  return (
    <section className="board" aria-labelledby="board-title">
      <div className="board-head">
        <div className="board-head-left">
          <h2 id="board-title">Routes, least punctual first</h2>
          <div className="mode-tabs" role="group" aria-label="Filter by transport mode">
            {modes.map((m) => (
              <button
                key={m}
                type="button"
                className={`mode-tab ${mode === m ? 'active' : ''}`}
                aria-pressed={mode === m}
                onClick={() => onSelectMode(m)}
              >
                {m === 'all' ? 'All' : m.charAt(0).toUpperCase() + m.slice(1)}
              </button>
            ))}
          </div>
        </div>
        <label className="search">
          <span className="visually-hidden">Find a route</span>
          <input
            type="search"
            placeholder="Filter route number or corridor"
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
                On time
              </th>
              <th scope="col" className="num">
                Avg delay
              </th>
              <th scope="col" className="num">
                Stops
              </th>
              <th scope="col" className="num">
                Cancellations
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
  const slug = modeSlug(route.routeType);

  return (
    <tr className="clickable-row" onClick={onRowClick}>
      <th scope="row">
        <Link to={link} className="route-cell-link" onClick={(e) => e.stopPropagation()}>
          <div className="route-inline">
            <span className={`route-badge badge-${slug}`}>
              {route.routeShortName || route.routeId}
            </span>
            <div className="route-meta-col">
              <span className="route-name" title={route.routeLongName || route.routeId}>
                {route.routeLongName || route.routeId}
              </span>
              <span className="route-mode-text">{formatMode(route.routeType)}</span>
            </div>
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
            <span className="rate-sample-count">
              {lowSample
                ? `low sample · ${route.observedStops}`
                : `${route.observedStops.toLocaleString('en-AU')} stops`}
            </span>
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

function formatShortDate(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', { day: 'numeric', month: 'short' });
}

function formatDay(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-AU', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  });
}
