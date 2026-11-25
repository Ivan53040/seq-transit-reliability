import { useEffect, useRef, useState, type RefObject } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import {
  ApiError,
  brisbaneToday,
  formatMode,
  loadRouteDetail,
  modeSlug,
  type RouteDetailReport,
  type RouteStopPerformance,
} from './api';
import { DailyTrendChart } from './components/DailyTrendChart';
import { HourlyPerformanceChart } from './components/HourlyPerformanceChart';
import {
  formatDay,
  formatDelay,
  formatOptionalDelay,
  formatShortDate,
  pageTitle,
  percent,
  rateBand,
} from './format';
import { sampleRouteDetail } from './sample';

type Load =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  | { state: 'ready'; report: RouteDetailReport };

export function RoutePage() {
  const { routeId } = useParams<{ routeId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();

  const today = brisbaneToday();
  const date = searchParams.get('date') || today;
  const isSample = searchParams.has('sample');

  const [load, setLoad] = useState<Load>({ state: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!routeId) return;

    if (isSample) {
      setLoad({ state: 'ready', report: sampleRouteDetail(routeId, date) });
      return;
    }

    const controller = new AbortController();
    setLoad({ state: 'loading' });

    loadRouteDetail(routeId, date, 7, controller.signal)
      .then((report) => setLoad({ state: 'ready', report }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        const message =
          error instanceof ApiError && error.status === 404
            ? `Route “${routeId}” could not be found. It may be inactive or have no recorded trips.`
            : error instanceof ApiError
              ? `API returned error ${error.status} for ${error.path}.`
              : 'Unable to connect to the transit reliability service.';
        setLoad({ state: 'error', message });
      });

    return () => controller.abort();
  }, [routeId, date, isSample, attempt]);

  const handleDateChange = (newDate: string) => {
    if (!newDate) return;
    const next = new URLSearchParams(searchParams);
    next.set('date', newDate);
    setSearchParams(next);
  };

  // Name the tab after the route, and move focus to its heading once loaded so
  // keyboard and screen reader users land on the new page rather than on <body>.
  const headingRef = useRef<HTMLHeadingElement | null>(null);
  const readySummary = load.state === 'ready' ? load.report.summary : null;
  useEffect(() => {
    if (!readySummary) {
      document.title = pageTitle(`Route ${routeId ?? ''}`);
      return;
    }
    const code = readySummary.routeShortName || readySummary.routeId;
    document.title = pageTitle(
      readySummary.routeLongName ? `Route ${code} · ${readySummary.routeLongName}` : `Route ${code}`,
    );
  }, [readySummary, routeId]);
  useEffect(() => {
    if (load.state === 'ready') headingRef.current?.focus({ preventScroll: true });
  }, [load.state, routeId]);

  const backQuery = new URLSearchParams(searchParams).toString();
  const backHref = `/${backQuery ? `?${backQuery}` : ''}`;

  return (
    <div className="route-detail-page">
      <nav className="detail-nav" aria-label="Breadcrumb">
        <Link to={backHref} className="back-link">
          <span aria-hidden="true">&larr;</span> All routes
        </Link>
      </nav>

      {load.state === 'loading' && (
        <div className="message" role="status">
          <p>Loading reliability metrics for route {routeId}…</p>
        </div>
      )}

      {load.state === 'error' && (
        <div className="message" role="alert">
          <p>{load.message}</p>
          <button type="button" onClick={() => setAttempt((n) => n + 1)}>
            Try again
          </button>
        </div>
      )}

      {load.state === 'ready' && (
        <>
          <RouteHeader
            report={load.report}
            date={date}
            today={today}
            onDateChange={handleDateChange}
            headingRef={headingRef}
          />

          <RouteMetricsStrip summary={load.report.summary} />

          <section className="charts-stack" aria-label="Performance charts">
            <div className="chart-band">
              <DailyTrendChart
                data={load.report.daily}
                fromDate={load.report.summary.fromDate}
                toDate={load.report.summary.toDate}
              />
            </div>

            <div className="chart-band">
              <HourlyPerformanceChart data={load.report.hours} dayLabel={formatDay(date)} />
            </div>
          </section>

          <section className="stops-section" aria-labelledby="stops-heading">
            <div className="stops-header">
              <div>
                <p className="section-kicker">Corridor stops · {formatDay(date)}</p>
                <h2 id="stops-heading">Stops, least punctual first</h2>
              </div>
            </div>

            {load.report.stops.length === 0 ? (
              <p className="board-empty">
                No individual stop delay observations recorded for this route on this date.
              </p>
            ) : (
              <StopsTable stops={load.report.stops} />
            )}
          </section>
        </>
      )}
    </div>
  );
}

function RouteHeader({
  report,
  date,
  today,
  onDateChange,
  headingRef,
}: {
  report: RouteDetailReport;
  date: string;
  today: string;
  onDateChange: (d: string) => void;
  headingRef: RefObject<HTMLHeadingElement | null>;
}) {
  const { summary } = report;
  const isToday = date === today;
  const slug = modeSlug(summary.routeType);

  return (
    <header className="route-page-header">
      <div className="route-identity">
        <div className="route-header-line">
          <span className={`route-badge large badge-${slug}`}>
            {summary.routeShortName || summary.routeId}
          </span>
          <span className="route-mode-label">{formatMode(summary.routeType)}</span>
          <span className="meta-sep" aria-hidden="true">·</span>
          <code className="route-id-code">{summary.routeId}</code>
        </div>
        <h1 className="route-title" ref={headingRef} tabIndex={-1}>
          {summary.routeLongName || `Route ${summary.routeShortName || summary.routeId}`}
        </h1>
        <p className="route-subtitle">
          Service day: <strong>{isToday ? 'Today' : formatDay(date)}</strong>
        </p>
      </div>

      <div className="route-date-picker">
        <label className="date-field">
          <span>Service day</span>
          <input
            type="date"
            value={date}
            max={today}
            onChange={(e) => e.target.value && onDateChange(e.target.value)}
          />
        </label>
      </div>
    </header>
  );
}

function RouteMetricsStrip({ summary }: { summary: RouteDetailReport['summary'] }) {
  const rate = summary.onTimeRate;
  const band = rate === null ? 'muted' : rateBand(rate);
  const period = `${formatShortDate(summary.fromDate)} – ${formatShortDate(summary.toDate)}`;

  return (
    <section className="metric-strip" aria-label={`7-day summary for ${period}`}>
      <div className="metric-cell metric-primary">
        <span className="metric-label">7-day on-time rate</span>
        <div className={`metric-value rate-${band}`}>
          {rate === null ? '—' : percent(rate)}
        </div>
        <span className="metric-hint">
          {summary.observedStops.toLocaleString('en-AU')} stops · {period}
        </span>
      </div>

      <div className="metric-cell">
        <span className="metric-label">Median delay</span>
        <div className="metric-value">{formatOptionalDelay(summary.medianDelaySeconds)}</div>
        <span className="metric-hint">50th percentile</span>
      </div>

      <div className="metric-cell">
        <span className="metric-label">P90 delay</span>
        <div className="metric-value">{formatOptionalDelay(summary.p90DelaySeconds)}</div>
        <span className="metric-hint">90th percentile buffer</span>
      </div>

      <div className="metric-cell">
        <span className="metric-label">Feed cancellations</span>
        <div className="metric-value">
          {summary.cancelledTrips === 0 ? <span className="muted">0</span> : summary.cancelledTrips}
        </div>
        <span className="metric-hint">Explicitly reported</span>
      </div>
    </section>
  );
}

function StopsTable({ stops }: { stops: RouteStopPerformance[] }) {
  return (
    <>
      <div className="desktop-table table-responsive">
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
            {stops.map((stop) => {
              const band = rateBand(stop.onTimeRate);
              return (
                <tr key={stop.stopId}>
                  <th scope="row">
                    <span className="stop-name">{stop.stopName || stop.stopId}</span>
                    <span className="stop-id muted">Stop #{stop.stopId}</span>
                  </th>
                  <td className="col-rate">
                    <div className={`rate rate-${band}`}>
                      <span className="rate-track" aria-hidden="true">
                        <span
                          className="rate-fill"
                          style={{ width: percent(stop.onTimeRate) }}
                        />
                      </span>
                      <span className="rate-value">{percent(stop.onTimeRate)}</span>
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

      <div className="mobile-cards" aria-label="Corridor stops list">
        {stops.map((stop) => {
          const band = rateBand(stop.onTimeRate);
          const pct = percent(stop.onTimeRate);
          return (
            <article key={stop.stopId} className="mobile-card">
              <div className="mobile-card-top">
                <div className="mobile-card-text">
                  <span className="mobile-card-name">{stop.stopName || stop.stopId}</span>
                  <span className="mobile-card-sub">Stop #{stop.stopId}</span>
                </div>
                <span className={`mobile-card-rate rate-${band}`}>{pct}</span>
              </div>
              <div className={`mobile-card-bar rate-${band}`} aria-hidden="true">
                <span className="rate-fill" style={{ width: pct }} />
              </div>
              <div className="mobile-card-meta">
                <span>{formatDelay(stop.averageDelaySeconds)} avg delay</span>
                <span>{stop.observedStops.toLocaleString('en-AU')} stops</span>
              </div>
            </article>
          );
        })}
      </div>
    </>
  );
}
