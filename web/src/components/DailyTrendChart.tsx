import type { DailyOnTime } from '../api';

interface DailyTrendChartProps {
  data: DailyOnTime[];
}

export function DailyTrendChart({ data }: DailyTrendChartProps) {
  if (data.length === 0) {
    return (
      <div className="chart-empty" role="note">
        <p>No daily performance data available for this route in the past 7 days.</p>
      </div>
    );
  }

  // Dimensions
  const width = 640;
  const height = 220;
  const padding = { top: 25, right: 30, bottom: 45, left: 45 };
  const innerWidth = width - padding.left - padding.right;
  const innerHeight = height - padding.top - padding.bottom;

  // Domain: Y-axis from 50% to 100% (or min rate - 10% to 100%)
  const minRate = Math.min(...data.map((d) => d.onTimeRate));
  const yMin = Math.max(0, Math.floor((minRate - 0.05) * 10) / 10);
  const yMax = 1.0;

  const getX = (index: number) => {
    if (data.length <= 1) return padding.left + innerWidth / 2;
    return padding.left + (index / (data.length - 1)) * innerWidth;
  };

  const getY = (rate: number) => {
    const clamped = Math.max(yMin, Math.min(yMax, rate));
    const normalized = (clamped - yMin) / (yMax - yMin || 1);
    return padding.top + innerHeight - normalized * innerHeight;
  };

  // Build path
  const points = data.map((d, i) => ({ x: getX(i), y: getY(d.onTimeRate), d }));
  const pathData = points.reduce((acc, p, i) => `${acc} ${i === 0 ? 'M' : 'L'} ${p.x} ${p.y}`, '');
  const areaData =
    points.length > 0
      ? `${pathData} L ${points[points.length - 1].x} ${padding.top + innerHeight} L ${points[0].x} ${padding.top + innerHeight} Z`
      : '';

  // Grid lines
  const gridLevels = [0.6, 0.75, 0.9, 1.0].filter((l) => l >= yMin);

  return (
    <div className="chart-wrapper" aria-labelledby="daily-chart-heading">
      <h3 id="daily-chart-heading" className="chart-title">
        7-Day On-Time Trend
      </h3>
      <p className="chart-subtitle">Daily percentage of stops served on schedule</p>

      <svg
        className="accessible-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="Line chart showing daily on-time performance over the past week"
      >
        <defs>
          <linearGradient id="trendGradient" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--jacaranda)" stopOpacity="0.25" />
            <stop offset="100%" stopColor="var(--jacaranda)" stopOpacity="0.0" />
          </linearGradient>
        </defs>

        {/* Horizontal grid lines & target references */}
        {gridLevels.map((level) => {
          const y = getY(level);
          return (
            <g key={level} className="chart-grid">
              <line
                x1={padding.left}
                y1={y}
                x2={width - padding.right}
                y2={y}
                stroke="var(--rule)"
                strokeDasharray={level === 0.9 ? '4 3' : '2 2'}
                strokeWidth={level === 0.9 ? 1.5 : 1}
              />
              <text x={padding.left - 8} y={y + 4} textAnchor="end" className="chart-axis-label">
                {Math.round(level * 100)}%
              </text>
            </g>
          );
        })}

        {/* Area fill */}
        {areaData && <path d={areaData} fill="url(#trendGradient)" />}

        {/* Trend line */}
        {pathData && (
          <path
            d={pathData}
            fill="none"
            stroke="var(--jacaranda)"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        )}

        {/* Data points & labels */}
        {points.map((p) => {
          const rateColor =
            p.d.onTimeRate >= 0.9 ? 'var(--good)' : p.d.onTimeRate >= 0.75 ? 'var(--fair)' : 'var(--poor)';
          const dayLabel = formatShortDay(p.d.serviceDate);

          return (
            <g key={p.d.serviceDate} className="chart-point">
              <circle cx={p.x} cy={p.y} r="4.5" fill="var(--surface)" stroke={rateColor} strokeWidth="2.5" />
              <text x={p.x} y={p.y - 10} textAnchor="middle" className="chart-val-label" fill="var(--ink)">
                {Math.round(p.d.onTimeRate * 100)}%
              </text>
              <text x={p.x} y={padding.top + innerHeight + 20} textAnchor="middle" className="chart-date-label">
                {dayLabel}
              </text>
              <text
                x={p.x}
                y={padding.top + innerHeight + 34}
                textAnchor="middle"
                className="chart-obs-label"
                fill="var(--muted)"
              >
                {p.d.observedStops} stops
              </text>
            </g>
          );
        })}
      </svg>

      {/* Screen reader table ensuring 100% accessible text presentation */}
      <div className="visually-hidden">
        <table>
          <caption>Daily on-time rate over the last 7 days</caption>
          <thead>
            <tr>
              <th scope="col">Date</th>
              <th scope="col">On-Time Rate</th>
              <th scope="col">Stops Observed</th>
              <th scope="col">Average Delay</th>
            </tr>
          </thead>
          <tbody>
            {data.map((d) => (
              <tr key={d.serviceDate}>
                <td>{d.serviceDate}</td>
                <td>{Math.round(d.onTimeRate * 100)}%</td>
                <td>{d.observedStops}</td>
                <td>{d.averageDelaySeconds} seconds</td>
              </tr>
            ))}
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
