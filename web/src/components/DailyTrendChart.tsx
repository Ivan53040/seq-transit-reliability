import type { DailyOnTime } from '../api';

interface DailyTrendChartProps {
  data: DailyOnTime[];
  /** First and last service day of the period (yyyy-mm-dd). Every day gets a slot, with or without data. */
  fromDate: string;
  toDate: string;
}

export function DailyTrendChart({ data, fromDate, toDate }: DailyTrendChartProps) {
  if (data.length === 0) {
    return (
      <div className="chart-empty" role="note">
        <p>No daily performance data available for this route in these 7 days.</p>
      </div>
    );
  }

  const width = 920;
  const height = 220;
  const padding = { top: 28, right: 32, bottom: 48, left: 44 };
  const innerWidth = width - padding.left - padding.right;
  const innerHeight = height - padding.top - padding.bottom;

  const byDate = new Map(data.map((d) => [d.serviceDate, d]));
  const slots = datesBetween(fromDate, toDate).map((date) => ({ date, d: byDate.get(date) }));

  const minRate = Math.min(...data.map((d) => d.onTimeRate));
  const yMin = Math.max(0, Math.floor((minRate - 0.1) * 10) / 10);
  const yMax = 1.0;

  const getX = (index: number) => {
    if (slots.length <= 1) return padding.left + innerWidth / 2;
    return padding.left + (index / (slots.length - 1)) * innerWidth;
  };

  const getY = (rate: number) => {
    const clamped = Math.max(yMin, Math.min(yMax, rate));
    const normalized = (clamped - yMin) / (yMax - yMin || 1);
    return padding.top + innerHeight - normalized * innerHeight;
  };

  const runs: { x: number; y: number; d: DailyOnTime }[][] = [];
  let current: { x: number; y: number; d: DailyOnTime }[] = [];
  slots.forEach((slot, i) => {
    if (slot.d) {
      current.push({ x: getX(i), y: getY(slot.d.onTimeRate), d: slot.d });
    } else if (current.length > 0) {
      runs.push(current);
      current = [];
    }
  });
  if (current.length > 0) runs.push(current);

  const points = runs.flat();
  const baseline = padding.top + innerHeight;
  const pathData = runs
    .map((run) => run.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.x} ${p.y}`).join(' '))
    .join(' ');

  const gridLevels = [0.25, 0.5, 0.75, 0.9, 1.0].filter((l) => l >= yMin);

  return (
    <div className="chart-wrapper" aria-labelledby="daily-chart-heading">
      <div className="chart-header-row">
        <div>
          <p className="section-kicker">7-day history</p>
          <h2 id="daily-chart-heading" className="chart-title">
            Daily on-time rate
          </h2>
        </div>
        <span className="chart-note">Dashed line marks 90% target · gaps indicate unrecorded days</span>
      </div>

      <svg
        className="accessible-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="Line chart showing daily on-time performance over the past week"
      >
        {/* Horizontal reference rules */}
        {gridLevels.map((level) => {
          const y = getY(level);
          const isTarget = level === 0.9;
          return (
            <g key={level}>
              <line
                x1={padding.left}
                y1={y}
                x2={width - padding.right}
                y2={y}
                stroke={isTarget ? 'var(--ink)' : 'var(--rule)'}
                strokeOpacity={isTarget ? 0.35 : 1}
                strokeDasharray={isTarget ? '4 4' : undefined}
                strokeWidth="1"
              />
              <text x={padding.left - 10} y={y + 4} textAnchor="end" className="chart-axis-label">
                {Math.round(level * 100)}%
              </text>
            </g>
          );
        })}

        {/* Baseline axis */}
        <line
          x1={padding.left}
          y1={baseline}
          x2={width - padding.right}
          y2={baseline}
          stroke="var(--ink)"
          strokeWidth="1.5"
        />

        {/* Crisp Swiss trend stroke (no gradient wash) */}
        {pathData && (
          <path
            d={pathData}
            fill="none"
            stroke="var(--ink)"
            strokeWidth="2.2"
            strokeLinejoin="miter"
          />
        )}

        {/* Geometric square markers + value labels */}
        {points.map((p) => {
          const rateColor =
            p.d.onTimeRate >= 0.9 ? 'var(--good)' : p.d.onTimeRate >= 0.75 ? 'var(--fair)' : 'var(--poor)';

          return (
            <g key={p.d.serviceDate}>
              <rect
                x={p.x - 4.5}
                y={p.y - 4.5}
                width="9"
                height="9"
                fill="var(--paper)"
                stroke={rateColor}
                strokeWidth="2.5"
              />
              <text x={p.x} y={p.y - 12} textAnchor="middle" className="chart-val-label">
                {Math.round(p.d.onTimeRate * 100)}%
              </text>
            </g>
          );
        })}

        {/* X-axis slots */}
        {slots.map((slot, i) => (
          <g key={slot.date}>
            <text x={getX(i)} y={baseline + 20} textAnchor="middle" className="chart-date-label">
              {formatShortDay(slot.date)}
            </text>
            <text x={getX(i)} y={baseline + 35} textAnchor="middle" className="chart-obs-label">
              {slot.d ? `${slot.d.observedStops.toLocaleString('en-AU')} stops` : '—'}
            </text>
          </g>
        ))}
      </svg>

      {/* Screen reader data table */}
      <div className="visually-hidden">
        <table>
          <caption>Daily on-time rate for each day in the period</caption>
          <thead>
            <tr>
              <th scope="col">Date</th>
              <th scope="col">On-Time Rate</th>
              <th scope="col">Stops Observed</th>
              <th scope="col">Average Delay</th>
            </tr>
          </thead>
          <tbody>
            {slots.map(({ date, d }) =>
              d ? (
                <tr key={date}>
                  <td>{date}</td>
                  <td>{Math.round(d.onTimeRate * 100)}%</td>
                  <td>{d.observedStops}</td>
                  <td>{d.averageDelaySeconds} seconds</td>
                </tr>
              ) : (
                <tr key={date}>
                  <td>{date}</td>
                  <td colSpan={3}>No data</td>
                </tr>
              ),
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function formatShortDay(isoDate: string): string {
  const d = new Date(`${isoDate}T00:00:00`);
  return d.toLocaleDateString('en-AU', { weekday: 'short', day: 'numeric' });
}

function datesBetween(from: string, to: string): string[] {
  const dates: string[] = [];
  const day = new Date(`${from}T00:00:00Z`);
  const end = new Date(`${to}T00:00:00Z`);
  while (day <= end && dates.length < 62) {
    dates.push(day.toISOString().slice(0, 10));
    day.setUTCDate(day.getUTCDate() + 1);
  }
  return dates;
}
