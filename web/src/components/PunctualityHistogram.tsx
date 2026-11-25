import type { RouteOnTime } from '../api';
import { useChartWidth } from './useChartWidth';

export type PunctualityBandKey = 'severe' | 'poor' | 'below' | 'fair' | 'good';

export interface PunctualityBand {
  readonly key: PunctualityBandKey;
  readonly label: string;
  readonly shortLabel: string;
  readonly min: number;
  readonly max: number;
  readonly tone: 'poor' | 'fair' | 'good';
}

export const PUNCTUALITY_BANDS: readonly PunctualityBand[] = [
  { key: 'severe', label: 'Under 50%', shortLabel: '<50%', min: 0, max: 0.5, tone: 'poor' },
  { key: 'poor', label: '50–64%', shortLabel: '50–64%', min: 0.5, max: 0.65, tone: 'poor' },
  { key: 'below', label: '65–74%', shortLabel: '65–74%', min: 0.65, max: 0.75, tone: 'poor' },
  { key: 'fair', label: '75–89%', shortLabel: '75–89%', min: 0.75, max: 0.9, tone: 'fair' },
  { key: 'good', label: '90–100%', shortLabel: '90%+', min: 0.9, max: 1.01, tone: 'good' },
];

export function matchesPunctualityBand(onTimeRate: number, bandKey: PunctualityBandKey): boolean {
  const band = PUNCTUALITY_BANDS.find((b) => b.key === bandKey);
  if (!band) return true;
  return onTimeRate >= band.min && onTimeRate < band.max;
}

interface PunctualityHistogramProps {
  routes: RouteOnTime[];
  activeBand: PunctualityBandKey | null;
  onSelectBand: (band: PunctualityBandKey | null) => void;
  scopeLabel: string;
}

export function PunctualityHistogram({
  routes,
  activeBand,
  onSelectBand,
  scopeLabel,
}: PunctualityHistogramProps) {
  const [frameRef, measuredWidth] = useChartWidth<HTMLDivElement>(460);
  const counts = PUNCTUALITY_BANDS.map((band) => ({
    band,
    count: routes.filter((r) => r.onTimeRate >= band.min && r.onTimeRate < band.max).length,
  }));

  const maxCount = Math.max(1, ...counts.map((c) => c.count));
  const width = Math.max(260, measuredWidth);
  const height = 164;
  const padding = { top: 26, right: 8, bottom: 30, left: 8 };
  const innerWidth = width - padding.left - padding.right;
  const innerHeight = height - padding.top - padding.bottom;
  const baseline = padding.top + innerHeight;
  const slotWidth = innerWidth / PUNCTUALITY_BANDS.length;
  const barWidth = Math.min(54, slotWidth * 0.62);

  const toggle = (key: PunctualityBandKey) => {
    onSelectBand(activeBand === key ? null : key);
  };

  return (
    <div className="pulse-col" aria-labelledby="histogram-heading">
      <div className="section-kicker-row">
        <span id="histogram-heading" className="section-kicker">
          Route spread · {scopeLabel} ({routes.length})
        </span>
        {activeBand ? (
          <button type="button" className="text-reset-btn" onClick={() => onSelectBand(null)}>
            Clear band
          </button>
        ) : (
          <span className="pulse-hint">Tap a bar to filter</span>
        )}
      </div>

      {/* role="group", not "img": the bars inside are real buttons and must stay reachable. */}
      <div ref={frameRef} className="chart-frame">
      <svg
        className="accessible-chart histogram-svg"
        width={width}
        height={height}
        viewBox={`0 0 ${width} ${height}`}
        role="group"
        aria-label={`Distribution of ${routes.length} routes across punctuality bands. Select a band to filter routes.`}
      >
        {/* Subtle mid-height reference rule */}
        <line
          x1={padding.left}
          y1={padding.top + innerHeight / 2}
          x2={width - padding.right}
          y2={padding.top + innerHeight / 2}
          stroke="var(--rule)"
          strokeDasharray="3 3"
          strokeWidth="1"
        />

        {/* Baseline */}
        <line
          x1={padding.left}
          y1={baseline}
          x2={width - padding.right}
          y2={baseline}
          stroke="var(--ink)"
          strokeWidth="1.5"
        />

        {counts.map(({ band, count }, idx) => {
          const xCenter = padding.left + idx * slotWidth + slotWidth / 2;
          const barX = xCenter - barWidth / 2;
          const barH = count === 0 ? 0 : Math.max(4, (count / maxCount) * innerHeight);
          const barY = baseline - barH;
          const isSelected = activeBand === band.key;
          const isDimmed = activeBand !== null && !isSelected;
          const fill =
            band.tone === 'good'
              ? 'var(--good)'
              : band.tone === 'fair'
                ? 'var(--fair)'
                : 'var(--poor)';

          return (
            <g
              key={band.key}
              className="chart-bar-slot"
              tabIndex={0}
              role="button"
              aria-pressed={isSelected}
              aria-label={`${band.label} on time: ${count} routes. Activate to filter routes.`}
              onClick={() => toggle(band.key)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  toggle(band.key);
                }
              }}
            >
              {/* Full-column hit area */}
              <rect
                x={padding.left + idx * slotWidth}
                y={0}
                width={slotWidth}
                height={height}
                fill="transparent"
              />

              {/* Keyboard focus ring (SVG groups don't draw a CSS outline reliably) */}
              <rect
                className="chart-focus-ring"
                x={padding.left + idx * slotWidth + 1.5}
                y={1.5}
                width={slotWidth - 3}
                height={height - 3}
                fill="none"
              />

              {/* Highlight backdrop when selected */}
              {isSelected && (
                <rect
                  x={padding.left + idx * slotWidth + 3}
                  y={4}
                  width={slotWidth - 6}
                  height={height - 8}
                  fill="var(--surface)"
                  stroke="var(--rule-strong)"
                  strokeWidth="1.5"
                />
              )}

              {/* Bar */}
              {barH > 0 && (
                <rect
                  x={barX}
                  y={barY}
                  width={barWidth}
                  height={barH}
                  fill={fill}
                  opacity={isDimmed ? 0.3 : 1}
                />
              )}

              {/* Count label above bar */}
              <text
                x={xCenter}
                y={barH > 0 ? barY - 7 : baseline - 7}
                textAnchor="middle"
                className="chart-val-label"
                opacity={isDimmed ? 0.45 : 1}
              >
                {count}
              </text>

              {/* Band label below baseline */}
              <text
                x={xCenter}
                y={baseline + 19}
                textAnchor="middle"
                className={`chart-hour-label ${isSelected ? 'selected-axis-label' : ''}`}
              >
                {band.shortLabel}
              </text>
            </g>
          );
        })}
      </svg>
      </div>

      <div className="visually-hidden">
        <table>
          <caption>Number of routes in each punctuality band</caption>
          <thead>
            <tr>
              <th scope="col">Punctuality band</th>
              <th scope="col">Routes</th>
            </tr>
          </thead>
          <tbody>
            {counts.map(({ band, count }) => (
              <tr key={band.key}>
                <td>{band.label}</td>
                <td>{count}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
