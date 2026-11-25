import { useLayoutEffect, useRef, useState } from 'react';

/**
 * Measures the chart container so the SVG can be laid out at its real pixel width.
 * Charts then draw text at a fixed, readable size instead of scaling a wide viewBox
 * down (a 920-unit viewBox on a 390px phone shrank 11px labels to about 5px).
 */
export function useChartWidth<T extends HTMLElement>(fallback: number) {
  const ref = useRef<T | null>(null);
  const [width, setWidth] = useState(fallback);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const next = Math.round(el.getBoundingClientRect().width);
      if (next > 0) setWidth((prev) => (prev === next ? prev : next));
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  return [ref, width] as const;
}
