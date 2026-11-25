import { useState } from 'react';
import type { RouteHourPerformance } from '../api';

interface HourlyPerformanceChartProps {
  data: RouteHourPerformance[];
}

export function HourlyPerformanceChart({ data }: HourlyPerformanceChartProps) {
  const [activeHour, setActiveHour] = useState<number | null>(null);

  if (data.length === 0) {
    return (
      <div className="chart-empty" role="note">
        <p>No hourly performance observations recorded for this route on this date.</p>
      </div>
    );
  }

  const width = 920;
  const height = 240;
  const padding = { top: 30, right: 20, bottom: 38, left: 44 };
  const innerWidth = width - padding.left - padding.right;
  const innerHeight = height - padding.top - padding.bottom;
  const baseline = padding.top + innerHeight;

  const sorted = [...data].sort((a, b) => a.hour - b.hour);
  const byHour = new Map(sorted.map((d) => [d.hour, d]));
  const slotWidth = innerWidth / 24;
  const barWidth = Math.max(14, Math.min(24, slotWidth * 0.66));

  const worst = sorted.reduce((acc, cur) => (cur.onTimeRate < acc.onTimeRate ? cur : acc), sorted[0]);
  const selected = activeHour !== null ? byHour.get(activeHour) ?? null : null;
  const readout = selected ?? worst;

  const getY = (rate: number) => {
    const clamped = Math.max(0, Math.min(1, rate));
    return padding.top + innerHeight - clamped * innerHeight;
  };

  const gridLevels = [0.25, 0.5, 0.75, 0.9, 1.0];

  const amPeakX = padding.left + 7 * slotWidth;
  const pmPeakX = padding.left + 16 * slotWidth;

  return (
    <div className="chart-wrapper" aria-labelledby="hourly-chart-heading">
      <div className="chart-header-row">
        <div>
          <p className="section-kicker">By hour of day · Brisbane time (AEST)</p>
          <h2 id="hourly-chart-heading" className="chart-title">
            Hourly punctuality
          </h2>
        </div>

        <div className="chart-readout" aria-live="polite">
          <span className="readout-tag">{selected ? 'Selected hour' : 'Slowest hour'}</span>
          <strong>{formatHour(readout.hour)}</strong>
          <span>{Math.round(readout.onTimeRate * 100)}% on time</span>
          <span className="meta-sep" aria-hidden="true">·</span>
          <span>{formatDelay(readout.averageDelaySeconds)} avg</span>
          <span className="meta-sep" aria-hidden="true">·</span>
          <span>{readout.observedStops.toLocaleString('en-AU')} stops</span>
        </div>
      </div>

      <svg
        className="accessible-chart"
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label="Bar chart showing on-time performance by hour of day in Brisbane time"
        onMouseLeave={() => setActiveHour(null)}
      >
        {/* Peak window architectural bands (7-9am, 4-6pm) */}
        <rect
          x={amPeakX}
          y={padding.top}
          width={slotWidth * 3}
          height={innerHeight}
          fill="var(--peak-band)"
        />
        <text x={amPeakX + slotWidth * 1.5} y={padding.top - 10} textAnchor="middle" className="chart-peak-tag">
          AM PEAK
        </text>

        <rect
          x={pmPeakX}
          y={padding.top}
          width={slotWidth * 3}
          height={innerHeight}
          fill="var(--peak-band)"
        />
        <text x={pmPeakX + slotWidth * 1.5} y={padding.top - 10} textAnchor="middle" className="chart-peak-tag">
          PM PEAK
        </text>

        {/* Horizontal grid lines */}
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

        {/* Crisp square-corner Swiss bars */}
        {sorted.map((d) => {
          const xCenter = padding.left + d.hour * slotWidth + slotWidth / 2;
          const barX = xCenter - barWidth / 2;
          const barY = getY(d.onTimeRate);
          const barH = baseline - barY;
          const isActive = activeHour === d.hour;

          const barColor =
            d.onTimeRate >= 0.9 ? 'var(--good)' : d.onTimeRate >= 0.75 ? 'var(--fair)' : 'var(--poor)';

          return (
            <g
              key={d.hour}
              className="chart-bar-slot"
              tabIndex={0}
              role="button"
              aria-label={`${formatHour(d.hour)}: ${Math.round(d.onTimeRate * 100)}% on time, ${formatDelay(d.averageDelaySeconds)} average delay, ${d.observedStops} stops`}
              onMouseEnter={() => setActiveHour(d.hour)}
              onFocus={() => setActiveHour(d.hour)}
              onBlur={() => setActiveHour(null)}
            >
              {/* Invisible full-height hit target for effortless hover */}
              <rect
                x={padding.left + d.hour * slotWidth}
                y={padding.top}
                width={slotWidth}
                height={innerHeight}
                fill="transparent"
              />

              <rect
                x={barX}
                y={barY}
                width={barWidth}
                height={Math.max(2, barH)}
                fill={barColor}
                opacity={activeHour === null || isActive ? 1 : 0.4}
              />

              {isActive && (
                <text x={xCenter} y={barY - 8} textAnchor="middle" className="chart-val-label">
                  {Math.round(d.onTimeRate * 100)}%
                </text>
              )}
            </g>
          );
        })}

        {/* Clean sparse 3-hour ticks */}
        {Array.from({ length: 8 }, (_, i) => i * 3).map((hour) => (
          <text
            key={hour}
            x={padding.left + hour * slotWidth + slotWidth / 2}
            y={baseline + 22}
            textAnchor="middle"
            className="chart-hour-label"
          >
            {formatHour(hour)}
          </text>
        ))}
      </svg>

      {/* Screen reader table for full accessibility */}
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
  if (hour === 0) return '12 am';
  if (hour < 12) return `${hour} am`;
  if (hour === 12) return '12 pm';
  return `${hour - 12} pm`;
}

function formatDelay(seconds: number): string {
  const minutes = seconds / 60;
  if (Math.abs(minutes) < 0.1) return 'on time';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}
