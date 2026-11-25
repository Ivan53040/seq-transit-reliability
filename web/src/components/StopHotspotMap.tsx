import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { WorstStopPerformance } from '../api';

interface StopHotspotMapProps {
  stops: WorstStopPerformance[];
  theme: 'light' | 'dark';
  query: string;
}

type RegionKey = 'all' | 'brisbane' | 'gold-coast' | 'sunshine-coast';

const REGIONS: readonly { key: RegionKey; label: string; center?: [number, number]; zoom?: number }[] = [
  { key: 'all', label: 'All SEQ' },
  { key: 'brisbane', label: 'Brisbane', center: [-27.4705, 153.026], zoom: 13 },
  { key: 'gold-coast', label: 'Gold Coast', center: [-28.002, 153.414], zoom: 12 },
  { key: 'sunshine-coast', label: 'Sunshine Coast', center: [-26.66, 153.09], zoom: 11 },
];

const DEFAULT_CENTER: [number, number] = [-27.4705, 153.026];

export function StopHotspotMap({ stops, theme, query }: StopHotspotMapProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const tileLayerRef = useRef<L.TileLayer | null>(null);
  const markerLayerRef = useRef<L.LayerGroup | null>(null);

  const [region, setRegion] = useState<RegionKey>('all');
  const [selectedStopId, setSelectedStopId] = useState<string | null>(null);

  const mappedStops = useMemo(
    () =>
      stops.filter(
        (s): s is WorstStopPerformance & { latitude: number; longitude: number } =>
          s.latitude !== null &&
          s.longitude !== null &&
          Number.isFinite(s.latitude) &&
          Number.isFinite(s.longitude),
      ),
    [stops],
  );

  const activeStop = useMemo(() => {
    if (selectedStopId) {
      const found = mappedStops.find((s) => s.stopId === selectedStopId);
      if (found) return found;
    }
    return mappedStops[0] ?? null;
  }, [mappedStops, selectedStopId]);

  // Initialise Leaflet map once per mount
  useEffect(() => {
    const el = containerRef.current;
    if (!el || mapRef.current) return;

    const map = L.map(el, {
      center: DEFAULT_CENTER,
      zoom: 11,
      scrollWheelZoom: false,
      zoomControl: true,
      attributionControl: true,
    });

    const markers = L.layerGroup().addTo(map);
    mapRef.current = map;
    markerLayerRef.current = markers;

    // Ensure tiles lay out cleanly after mount / viewport resize
    const resizeObserver = new ResizeObserver(() => {
      map.invalidateSize({ animate: false });
    });
    resizeObserver.observe(el);

    return () => {
      resizeObserver.disconnect();
      map.remove();
      mapRef.current = null;
      tileLayerRef.current = null;
      markerLayerRef.current = null;
    };
  }, []);

  // Swap minimal CARTO basemap whenever Light/Dark theme changes
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    if (tileLayerRef.current) {
      tileLayerRef.current.remove();
    }

    const tileUrl =
      theme === 'dark'
        ? 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png'
        : 'https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png';

    tileLayerRef.current = L.tileLayer(tileUrl, {
      subdomains: 'abcd',
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap &copy; CARTO',
    }).addTo(map);
  }, [theme]);

  // Render proportional Swiss signal circle markers
  useEffect(() => {
    const map = mapRef.current;
    const layer = markerLayerRef.current;
    if (!map || !layer) return;

    layer.clearLayers();
    if (mappedStops.length === 0) return;

    const stroke = theme === 'dark' ? '#0c0d0d' : '#ffffff';
    const selectedStroke = theme === 'dark' ? '#f3f3f1' : '#0b0b0b';

    // Draw higher-delay (earlier in list) markers last so the worst bottlenecks stay on top
    const ordered = [...mappedStops].reverse();

    for (const stop of ordered) {
      const isSelected = stop.stopId === selectedStopId;
      const color = markerColor(stop.onTimeRate, theme);
      const radius = Math.max(6, Math.min(15, 5 + Math.sqrt(stop.observedStops) * 0.42));

      const marker = L.circleMarker([stop.latitude, stop.longitude], {
        radius: isSelected ? radius + 2.5 : radius,
        fillColor: color,
        color: isSelected ? selectedStroke : stroke,
        weight: isSelected ? 2.5 : 1.5,
        fillOpacity: isSelected ? 0.96 : 0.78,
      });

      marker.bindPopup(
        `<div class="map-popup">
          <div class="map-popup-title">${escapeHtml(stop.stopName || stop.stopId)}</div>
          <div class="map-popup-meta">Stop #${escapeHtml(stop.stopId)}</div>
          <div class="map-popup-stats">
            <strong>${Math.round(stop.onTimeRate * 100)}%</strong> on time ·
            <strong>${formatDelay(stop.averageDelaySeconds)}</strong> avg ·
            ${stop.observedStops.toLocaleString('en-AU')} stops
          </div>
        </div>`,
        { closeButton: false, offset: [0, -4] },
      );

      marker.on('click', () => {
        setSelectedStopId(stop.stopId);
      });

      marker.addTo(layer);
    }
  }, [mappedStops, theme, selectedStopId]);

  // Fit bounds when region is 'all' or when search query narrows results
  useEffect(() => {
    const map = mapRef.current;
    if (!map || mappedStops.length === 0) return;

    if (region !== 'all' && !query.trim()) {
      const preset = REGIONS.find((r) => r.key === region);
      if (preset?.center && preset.zoom) {
        map.setView(preset.center, preset.zoom, { animate: true });
      }
      return;
    }

    const bounds = L.latLngBounds(mappedStops.map((s) => [s.latitude, s.longitude] as [number, number]));
    map.fitBounds(bounds, { padding: [36, 36], maxZoom: 14, animate: false });
  }, [mappedStops, region, query]);

  const handleRegionClick = (key: RegionKey) => {
    setRegion(key);
    const map = mapRef.current;
    if (!map) return;

    if (key === 'all') {
      if (mappedStops.length > 0) {
        const bounds = L.latLngBounds(mappedStops.map((s) => [s.latitude, s.longitude] as [number, number]));
        map.fitBounds(bounds, { padding: [36, 36], maxZoom: 14, animate: true });
      }
      return;
    }

    const preset = REGIONS.find((r) => r.key === key);
    if (preset?.center && preset.zoom) {
      map.setView(preset.center, preset.zoom, { animate: true });
    }
  };

  if (mappedStops.length === 0) {
    return (
      <p className="board-empty">
        {query
          ? `No mapped bottleneck stop matches “${query}”.`
          : 'No stop coordinates available for this service day yet.'}
      </p>
    );
  }

  const activeBand =
    activeStop && activeStop.onTimeRate >= 0.9
      ? 'good'
      : activeStop && activeStop.onTimeRate >= 0.75
        ? 'fair'
        : 'poor';

  return (
    <div className="map-section">
      <div className="map-toolbar">
        <div className="region-tabs" role="group" aria-label="Jump to region">
          {REGIONS.map((r) => (
            <button
              key={r.key}
              type="button"
              className={`region-tab ${region === r.key ? 'active' : ''}`}
              aria-pressed={region === r.key}
              onClick={() => handleRegionClick(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>

        <div className="map-legend" aria-label="Map colour legend">
          <span className="legend-item">
            <span className="legend-dot dot-poor" aria-hidden="true" />
            &lt;75%
          </span>
          <span className="legend-item">
            <span className="legend-dot dot-fair" aria-hidden="true" />
            75–89%
          </span>
          <span className="legend-item">
            <span className="legend-dot dot-good" aria-hidden="true" />
            90%+
          </span>
          <span className="legend-size-note">· size = volume</span>
        </div>
      </div>

      <div className="map-frame">
        <div
          ref={containerRef}
          className="hotspot-map-canvas"
          role="region"
          aria-label={`Interactive map of ${mappedStops.length} highest-delay transit stops across South East Queensland`}
        />
      </div>

      {activeStop && (
        <div className="map-inspector" aria-live="polite">
          <div className="map-inspector-main">
            <span className="map-inspector-tag">
              {selectedStopId ? 'Selected stop' : 'Highest avg delay'}
            </span>
            <strong className="map-inspector-name">{activeStop.stopName || activeStop.stopId}</strong>
            <span className="map-inspector-id muted">Stop #{activeStop.stopId}</span>
          </div>
          <div className="map-inspector-stats">
            <span className={`map-inspector-rate rate-${activeBand}`}>
              {Math.round(activeStop.onTimeRate * 100)}% on time
            </span>
            <span className="meta-sep" aria-hidden="true">·</span>
            <span>{formatDelay(activeStop.averageDelaySeconds)} avg</span>
            <span className="meta-sep" aria-hidden="true">·</span>
            <span>{activeStop.observedStops.toLocaleString('en-AU')} stops</span>
          </div>
        </div>
      )}
    </div>
  );
}

function markerColor(onTimeRate: number, theme: 'light' | 'dark'): string {
  if (onTimeRate >= 0.9) return theme === 'dark' ? '#34c77b' : '#0f7b3e';
  if (onTimeRate >= 0.75) return theme === 'dark' ? '#f0ad3e' : '#b26700';
  return theme === 'dark' ? '#f25f5c' : '#c81d25';
}

function formatDelay(seconds: number): string {
  const minutes = seconds / 60;
  if (Math.abs(minutes) < 0.1) return 'on time';
  const sign = minutes > 0 ? '+' : '−';
  return `${sign}${Math.abs(minutes).toFixed(1)} min`;
}

function escapeHtml(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}
