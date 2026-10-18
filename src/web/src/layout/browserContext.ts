import type { Snapshot } from '../contracts/generated';
import type { LayoutContext } from './inspectLayout';

/** Text measured with a canvas in the page's own fonts, and natural picture sizes read from the approved bytes. */
export function createBrowserLayoutContext(assetUrl: (snapshot: Snapshot, assetId: string) => string) {
  const canvas = document.createElement('canvas').getContext('2d')!;
  const sizes = new Map<string, { width: number; height: number } | null>();

  return async (snapshot: Snapshot, options: { includeAssets?: boolean } = {}): Promise<Pick<LayoutContext, 'measureWidth' | 'assetSize'>> => {
    await document.fonts.ready;
    const wanted = new Set<string>();
    if (options.includeAssets !== false) for (const p of snapshot.document.pages) for (const e of p.elements) if (e.kind === 'image') wanted.add(e.assetId);
    await Promise.all([...wanted].map(async id => {
      const url = assetUrl(snapshot, id);
      if (!url || sizes.has(url)) return;
      sizes.set(url, await new Promise(resolve => {
        const img = new Image();
        img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
        img.onerror = () => resolve(null);
        img.src = url;
      }));
    }));
    return {
      // font sizes are points and the result is points, so the canvas can be told "px" and the units cancel
      measureWidth: (text, f) => { canvas.font = `${f.italic ? 'italic ' : ''}${f.bold ? 'bold ' : ''}${f.sizePt}px "${f.family}"`; return canvas.measureText(text).width; },
      assetSize: id => sizes.get(assetUrl(snapshot, id)) ?? undefined,
    };
  };
}
