import { TextShape, type AbstractCanvas2D } from '@maxgraph/core';
import { wrapTextLines } from '../layout/textMeasurement';
let measure: CanvasRenderingContext2D | null = null;

/** maxGraph wraps only HTML labels. Keep plain SVG (including PNG export) and project line breaks without changing editable/canonical values. */
export class WrappedSvgText extends TextShape {
  override paint(canvas: AbstractCanvas2D, update = false): void {
    const original = this.value;
    if (typeof original !== 'string' || this.style?.whiteSpace !== 'wrap' || !this.bounds || this.bounds.width <= 0) { super.paint(canvas, update); return; }
    measure ??= document.createElement('canvas').getContext('2d');
    if (!measure) { super.paint(canvas, update); return; }
    measure.font = `${(this.fontStyle & 2) ? 'italic ' : ''}${(this.fontStyle & 1) ? 'bold ' : ''}${this.size}px "${this.family}"`;
    const width = this.bounds.width / this.scale;
    this.value = wrapTextLines(original, width, s => measure!.measureText(s).width).lines.join('\n');
    try { super.paint(canvas, update); } finally { this.value = original; }
  }
}
