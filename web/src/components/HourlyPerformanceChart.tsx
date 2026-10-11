import type { RouteHourPerformance } from '../api';

interface HourlyPerformanceChartProps {
  data: RouteHourPerformance[];
}

export function HourlyPerformanceChart({ data }: HourlyPerformanceChartProps) {
  if (data.length === 0) {
    return (
      <div className="chart-empty" role="note">
        <p>No hourly performance observations recorded for this route on this date.</p>
      </div>
    );
  }

  const width = 640;
  const height = 240;
  const padding = { top: 35, right: 25, bottom: 45, left: 45 };
  const innerWidth = width - padding.left - padding.right;
  const innerHeight = height - padding.top - padding.bottom;

  // Sorted hours
  const sorted = [...data].sort((a, b) => a.hour - b.hour);
  const barCount = sorted.length;
  const slotWidth = innerWidth / (barCount || 1);
  const barWidth = Math.max(12, Math.min(28, slotWidth * 0.75));

  // Y-axis: 0% to 100%
  const getY = (rate: number) => {
    const clamped = Math.max(0, Math.min(1, rate));
    return padding.top + innerHeight - clamped * innerHeight;
  };

  const gridLevels = [0.25, 0.5, 0.75, 0.9, 1.0];

  return (
    <div className="chart-wrapper" aria-labelledby="hourly-chart-heading">
      <div className="chart-header-row">
        <div>
          <h3 id="hourly-chart-heading" className="chart-title">
            Hourly Punctuality & Peak Congestion
          </h3>
          <p className="chart-subtitle">On-time rate by hour of day (Brisbane Time AEST)</p>
        </div>
        <div className="chart-legend" aria-hidden="true">
          <span className="legend-item">
            <span className="legend-swatch peak-swatch" /> Peak Period
          </span>
        </div>
      </div>

      <svg
        className="accessible-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="Bar chart showing on-time performance by hour of day in Brisbane time"
      >
        {/* Horizontal grid lines */}
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

        {/* Highlight Peak Bands & Render Bars */}
        {sorted.map((d, i) => {
          const isMorningPeak = d.hour >= 7 && d.hour <= 9;
          const isEveningPeak = d.hour >= 16 && d.hour <= 18;
          const isPeak = isMorningPeak || isEveningPeak;

          const xCenter = padding.left + i * slotWidth + slotWidth / 2;
          const barX = xCenter - barWidth / 2;
          const barY = getY(d.onTimeRate);
          const barH = padding.top + innerHeight - barY;

          const barColor =
            d.onTimeRate >= 0.9 ? 'var(--good)' : d.onTimeRate >= 0.75 ? 'var(--fair)' : 'var(--poor)';

          return (
            <g key={d.hour} className="chart-hour-group">
              {/* Peak background highlight */}
              {isPeak && (
                <rect
                  x={xCenter - slotWidth / 2}
                  y={padding.top}
                  width={slotWidth}
                  height={innerHeight}
                  fill="var(--jacaranda)"
                  opacity="0.08"
                />
              )}

              {/* Bar */}
              <rect
                x={barX}
                y={barY}
                width={barWidth}
                height={Math.max(2, barH)}
                rx="3"
                fill={barColor}
                className="chart-bar"
              />

              {/* Value on top of bar */}
              <text x={xCenter} y={barY - 6} textAnchor="middle" className="chart-bar-val">
                {Math.round(d.onTimeRate * 100)}%
              </text>

              {/* Hour label */}
              <text x={xCenter} y={padding.top + innerHeight + 18} textAnchor="middle" className="chart-hour-label">
                {formatHour(d.hour)}
              </text>

              {/* Observation count */}
              <text x={xCenter} y={padding.top + innerHeight + 32} textAnchor="middle" className="chart-obs-label">
                {d.observedStops}
              </text>
            </g>
          );
        })}
      </svg>

      {/* Screen reader table for full accessibility compliance */}
      <div className="visually-hidden">
        <table>
          <caption>Hourly on-time performance for this service day</caption>
          <thead>
            <tr>
              <th scope="col">Hour</th>
              <th scope="col">On-Time Rate</th>
              <th scope="col">Average Delay</th>
              <th scope="col">Stops Observed</th>
              <th scope="col">Peak Status</th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((d) => (
              <tr key={d.hour}>
                <td>{formatHour(d.hour)}</td>
                <td>{Math.round(d.onTimeRate * 100)}%</td>
                <td>{d.averageDelaySeconds} seconds</td>
                <td>{d.observedStops}</td>
                <td>{(d.hour >= 7 && d.hour <= 9) || (d.hour >= 16 && d.hour <= 18) ? 'Peak' : 'Off-peak'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function formatHour(hour: number): string {
  if (hour === 0) return '12am';
  if (hour < 12) return `${hour}am`;
  if (hour === 12) return '12pm';
  return `${hour - 12}pm`;
}
