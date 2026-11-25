import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import {
  ApiError,
  brisbaneToday,
  formatMode,
  loadRouteDetail,
  type RouteDetailReport,
  type RouteStopPerformance,
} from './api';
import { DailyTrendChart } from './components/DailyTrendChart';
import { HourlyPerformanceChart } from './components/HourlyPerformanceChart';
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

  const backQuery = new URLSearchParams(searchParams).toString();
  const backHref = `/${backQuery ? `?${backQuery}` : ''}`;

  return (
    <div className="route-detail-page">
      <nav className="detail-nav" aria-label="Breadcrumb">
        <Link to={backHref} className="back-link">
          <span aria-hidden="true">&larr;</span> Back to all routes
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
          />

          <RouteKpiCards summary={load.report.summary} />

          <section className="analytics-grid">
            <div className="analytics-card">
              <DailyTrendChart
                data={load.report.daily}
                fromDate={load.report.summary.fromDate}
                toDate={load.report.summary.toDate}
              />
            </div>

            <div className="analytics-card">
              <HourlyPerformanceChart data={load.report.hours} />
            </div>
          </section>

          <section className="stops-section" aria-labelledby="stops-heading">
            <div className="stops-header">
              <div>
                <h2 id="stops-heading">Bottleneck Stops on this Route</h2>
                <p className="stops-subtitle">
                  Stops with the lowest on-time rates on {formatDay(date)}, worst first
                </p>
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
}: {
  report: RouteDetailReport;
  date: string;
  today: string;
  onDateChange: (d: string) => void;
}) {
  const { summary } = report;
  const isToday = date === today;

  return (
    <header className="route-page-header">
      <div className="route-identity">
        <div className="route-header-line">
          <span className="route-badge large">{summary.routeShortName || summary.routeId}</span>
          <span className="route-mode large">{formatMode(summary.routeType)}</span>
        </div>
        <h1 className="route-title">
          {summary.routeLongName || `Route ${summary.routeShortName || summary.routeId}`}
        </h1>
        <p className="route-subtitle">
          Internal ID: <code>{summary.routeId}</code> &bull; Showing performance for{' '}
          <strong>{isToday ? 'Today' : formatDay(date)}</strong>
        </p>
      </div>

      <div className="route-date-picker">
        <label className="date-field">
          <span>Service Day</span>
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

function RouteKpiCards({ summary }: { summary: RouteDetailReport['summary'] }) {
  const rate = summary.onTimeRate;
  const band = rate === null ? 'none' : rate >= 0.9 ? 'good' : rate >= 0.75 ? 'fair' : 'poor';
  const period = `${formatShortDate(summary.fromDate)} – ${formatShortDate(summary.toDate)}`;

  return (
    <section className="kpi-grid" aria-label={`Performance for ${period}`}>
      <div className="kpi-card">
        <span className="kpi-label">7-Day On-Time Rate</span>
        <div className={`kpi-value rate-${band}`}>{rate === null ? 'No data' : `${Math.round(rate * 100)}%`}</div>
        <span className="kpi-hint">
          {summary.observedStops.toLocaleString('en-AU')} stops observed, {period}
        </span>
      </div>

      <div className="kpi-card">
        <span className="kpi-label">Typical (Median) Delay</span>
        <div className="kpi-value">{formatOptionalDelay(summary.medianDelaySeconds)}</div>
        <span className="kpi-hint">Half of observed stops were within this delay</span>
      </div>

      <div className="kpi-card">
        <span className="kpi-label">P90 Risk Delay</span>
        <div className="kpi-value">{formatOptionalDelay(summary.p90DelaySeconds)}</div>
        <span className="kpi-hint">90% of observed stops were within this delay</span>
      </div>

      <div className="kpi-card">
        <span className="kpi-label">Feed-Reported Cancellations</span>
        <div className="kpi-value">
          {summary.cancelledTrips === 0 ? (
            <span className="muted">0</span>
          ) : (
            summary.cancelledTrips
          )}
        </div>
        <span className="kpi-hint">Recorded {period}; zero does not confirm all trips ran</span>
      </div>
    </section>
  );
}

function StopsTable({ stops }: { stops: RouteStopPerformance[] }) {
  return (
    <div className="table-responsive">
      <table>
        <thead>
          <tr>
            <th scope="col">Stop Name</th>
            <th scope="col" className="col-rate">
              On time
            </th>
            <th scope="col" className="num">
              Average delay
            </th>
            <th scope="col" className="num">
              Observations
            </th>
          </tr>
        </thead>
        <tbody>
          {stops.map((stop) => {
            const band =
              stop.onTimeRate >= 0.9 ? 'good' : stop.onTimeRate >= 0.75 ? 'fair' : 'poor';
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
                        style={{ width: `${Math.round(stop.onTimeRate * 100)}%` }}
                      />
                    </span>
                    <span className="rate-value">{Math.round(stop.onTimeRate * 100)}%</span>
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
  );
}

function formatDelay(seconds: number): string {
  const minutes = seconds / 60;
  if (Math.abs(minutes) < 0.1) return 'on time';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}

function formatOptionalDelay(seconds: number | null): string {
  return seconds === null ? '—' : formatDelay(seconds);
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
