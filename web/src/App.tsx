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
  type WorstStopPerformance,
} from './api';
import { RoutePage } from './RoutePage';
import { sampleDay } from './sample';

const ROWS_SHOWN_AT_FIRST = 25;
const MIN_RELIABLE_SAMPLE = 20;

const CANONICAL_MODES = [
  { routeType: 3, key: 'bus', label: 'Bus' },
  { routeType: 2, key: 'train', label: 'Train' },
  { routeType: 4, key: 'ferry', label: 'Ferry' },
  { routeType: 0, key: 'tram', label: 'Tram' },
] as const;

type Theme = 'light' | 'dark';
type BoardTab = 'routes' | 'stops';
type SortKey = 'onTimeRate' | 'averageDelaySeconds' | 'observedStops' | 'cancellations';
type SortDir = 'asc' | 'desc';

type Load =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  | { state: 'ready'; report: DayReport };

function getInitialTheme(): Theme {
  if (typeof window === 'undefined') return 'light';
  const stored = window.localStorage.getItem('seq-theme');
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function App() {
  const [searchParams] = useSearchParams();
  const useSample = searchParams.has('sample');
  const [theme, setTheme] = useState<Theme>(getInitialTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    window.localStorage.setItem('seq-theme', theme);
  }, [theme]);

  const toggleTheme = () => setTheme((prev) => (prev === 'dark' ? 'light' : 'dark'));

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
        <div className="masthead-right">
          <span className="tagline">South East Queensland · GTFS-RT</span>
          <button
            type="button"
            className="theme-toggle"
            onClick={toggleTheme}
            aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} appearance`}
          >
            <span className="theme-dot" aria-hidden="true" />
            {theme === 'dark' ? 'Light' : 'Dark'}
          </button>
        </div>
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

  const buildRouteLink = (routeId: string) => {
    const params = new URLSearchParams(searchParams);
    params.set('date', date);
    return `/routes/${encodeURIComponent(routeId)}?${params.toString()}`;
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
        <WeeklyBrief summary={load.report.weeklySummary} routeLink={buildRouteLink} />
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
        <RouteBoard
          report={load.report}
          mode={activeMode}
          onSelectMode={handleModeSelect}
          routeLink={buildRouteLink}
        />
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

function WeeklyBrief({
  summary,
  routeLink,
}: {
  summary: NonNullable<DayReport['weeklySummary']>;
  routeLink: (routeId: string) => string;
}) {
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
              <dd>
                <Link to={routeLink(f.bestRoute.routeId)} className="brief-route-link">
                  {f.bestRoute.routeShortName} ({f.bestRoute.onTimePercent}%)
                </Link>
              </dd>
            </div>
          )}
          {f.worstRoute && (
            <div>
              <dt>Worst route</dt>
              <dd>
                <Link to={routeLink(f.worstRoute.routeId)} className="brief-route-link">
                  {f.worstRoute.routeShortName} ({f.worstRoute.onTimePercent}%)
                </Link>
              </dd>
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
  mode,
  onSelectMode,
  routeLink,
}: {
  report: DayReport;
  mode: string;
  onSelectMode: (mode: string) => void;
  routeLink: (routeId: string) => string;
}) {
  const [activeTab, setActiveTab] = useState<BoardTab>('routes');
  const [showAll, setShowAll] = useState(false);
  const [sortKey, setSortKey] = useState<SortKey>('onTimeRate');
  const [sortDir, setSortDir] = useState<SortDir>('asc');
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

  const handleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      // Sensible defaults: worst on-time first (asc); highest delay / stops / cancellations first (desc).
      setSortDir(key === 'onTimeRate' ? 'asc' : 'desc');
    }
  };

  const sortedRoutes = useMemo(() => {
    const filtered = report.routes.filter((r) => {
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

    const compare = (a: RouteOnTime, b: RouteOnTime) => {
      const factor = sortDir === 'asc' ? 1 : -1;
      const valA =
        sortKey === 'cancellations' ? (cancelled.get(a.routeId) ?? 0) : a[sortKey];
      const valB =
        sortKey === 'cancellations' ? (cancelled.get(b.routeId) ?? 0) : b[sortKey];
      if (valA !== valB) return (valA - valB) * factor;
      return b.observedStops - a.observedStops;
    };

    // Keep low-sample routes below reliable routes when sorting by punctuality/delay.
    if (sortKey === 'onTimeRate' || sortKey === 'averageDelaySeconds') {
      const reliable = filtered.filter((r) => r.observedStops >= MIN_RELIABLE_SAMPLE).sort(compare);
      const low = filtered.filter((r) => r.observedStops < MIN_RELIABLE_SAMPLE).sort(compare);
      return [...reliable, ...low];
    }

    return [...filtered].sort(compare);
  }, [report.routes, mode, query, sortKey, sortDir, cancelled]);

  const filteredStops = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return report.worstStops;
    return report.worstStops.filter(
      (s) => s.stopName.toLowerCase().includes(q) || s.stopId.toLowerCase().includes(q),
    );
  }, [report.worstStops, query]);

  const rows =
    showAll || query || mode !== 'all' ? sortedRoutes : sortedRoutes.slice(0, ROWS_SHOWN_AT_FIRST);

  const ariaSort = (key: SortKey): 'ascending' | 'descending' | 'none' =>
    sortKey === key ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none';

  const sortIndicator = (key: SortKey) =>
    sortKey === key ? (sortDir === 'asc' ? ' ↑' : ' ↓') : '';

  return (
    <section className="board" aria-labelledby="board-title">
      <div className="board-view-bar">
        <div className="view-tabs" role="tablist" aria-label="Board view">
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'routes'}
            className={`view-tab ${activeTab === 'routes' ? 'active' : ''}`}
            onClick={() => setActiveTab('routes')}
          >
            Routes <span className="tab-count">{sortedRoutes.length}</span>
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'stops'}
            className={`view-tab ${activeTab === 'stops' ? 'active' : ''}`}
            onClick={() => setActiveTab('stops')}
          >
            Worst stops <span className="tab-count">{report.worstStops.length}</span>
          </button>
        </div>

        <label className="search">
          <span className="visually-hidden">
            {activeTab === 'routes' ? 'Filter route or corridor' : 'Filter stop name or ID'}
          </span>
          <input
            type="search"
            placeholder={activeTab === 'routes' ? 'Filter route or corridor' : 'Filter stop name or ID'}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
      </div>

      {activeTab === 'routes' ? (
        <>
          <div className="board-subhead">
            <h2 id="board-title" className="visually-hidden">
              Routes ranked by punctuality
            </h2>
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
            <span className="board-sort-hint">Click any column header to sort</span>
          </div>

          <div className="table-responsive">
            <table>
              <thead>
                <tr>
                  <th scope="col">Route</th>
                  <th scope="col" className="col-rate" aria-sort={ariaSort('onTimeRate')}>
                    <button type="button" className="sort-btn" onClick={() => handleSort('onTimeRate')}>
                      On time{sortIndicator('onTimeRate')}
                    </button>
                  </th>
                  <th scope="col" className="num" aria-sort={ariaSort('averageDelaySeconds')}>
                    <button type="button" className="sort-btn" onClick={() => handleSort('averageDelaySeconds')}>
                      Avg delay{sortIndicator('averageDelaySeconds')}
                    </button>
                  </th>
                  <th scope="col" className="num" aria-sort={ariaSort('observedStops')}>
                    <button type="button" className="sort-btn" onClick={() => handleSort('observedStops')}>
                      Stops{sortIndicator('observedStops')}
                    </button>
                  </th>
                  <th scope="col" className="num" aria-sort={ariaSort('cancellations')}>
                    <button type="button" className="sort-btn" onClick={() => handleSort('cancellations')}>
                      Cancellations{sortIndicator('cancellations')}
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody>
                {rows.map((route) => {
                  const link = routeLink(route.routeId);
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

          {!showAll && !query && mode === 'all' && sortedRoutes.length > rows.length && (
            <button type="button" className="show-all" onClick={() => setShowAll(true)}>
              Show all {sortedRoutes.length} routes
            </button>
          )}
        </>
      ) : (
        <WorstStopsBoard stops={filteredStops} query={query} />
      )}
    </section>
  );
}

function WorstStopsBoard({ stops, query }: { stops: WorstStopPerformance[]; query: string }) {
  return (
    <>
      <div className="board-subhead">
        <p className="stops-board-note">
          Highest average arrival delay across South East Queensland (minimum 20 observed stops).
        </p>
      </div>
      {stops.length === 0 ? (
        <p className="board-empty">
          {query
            ? `No bottleneck stop matches “${query}”.`
            : 'No stops have reached 20 observations for this service day yet.'}
        </p>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th scope="col">Stop</th>
                <th scope="col" className="col-rate">
                  On time
                </th>
                <th scope="col" className="num">
                  Avg delay
                </th>
                <th scope="col" className="num">
                  Stops
                </th>
              </tr>
            </thead>
            <tbody>
              {stops.map((stop, index) => {
                const band =
                  stop.onTimeRate >= 0.9 ? 'good' : stop.onTimeRate >= 0.75 ? 'fair' : 'poor';
                return (
                  <tr key={stop.stopId}>
                    <th scope="row">
                      <div className="stop-rank-cell">
                        <span className="stop-rank-num">{String(index + 1).padStart(2, '0')}</span>
                        <div>
                          <span className="stop-name">{stop.stopName || stop.stopId}</span>
                          <span className="stop-id muted">Stop #{stop.stopId}</span>
                        </div>
                      </div>
                    </th>
                    <td className="col-rate">
                      <div className={`rate rate-${band}`}>
                        <span className="rate-track" aria-hidden="true">
                          <span
                            className="rate-fill"
                            style={{ width: percent(stop.onTimeRate) }}
                          />
                        </span>
                        <div className="rate-figures">
                          <span className="rate-value">{percent(stop.onTimeRate)}</span>
                          <span className="rate-sample-count">
                            {stop.observedStops.toLocaleString('en-AU')} stops
                          </span>
                        </div>
                      </div>
                    </td>
                    <td className="num">{formatDelay(stop.averageDelaySeconds)}</td>
                    <td className="num">{stop.observedStops.toLocaleString('en-AU')}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
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
