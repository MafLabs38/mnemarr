/**
 * Draws a Mermaid diagram (the ```mermaid blocks of a note) as an SVG image.
 *
 * A note is untrusted text, so the diagram is never put in the page as markup: Mermaid is asked for its strictest mode (it sanitises
 * labels and refuses click handlers and scripts), and what it returns is turned into an image (`<img src="data:image/svg+xml...">`).
 * An SVG shown as an image cannot run a script or load anything, whatever it contains. The library is large, so it is only fetched
 * when a note holds a diagram.
 */

/** The longest diagram drawn: Mermaid's own limit is higher, and a note can be hostile. */
export const MAX_DIAGRAM_CHARS = 20_000;

const FONT = 'trebuchet ms, verdana, arial, sans-serif';

type MermaidApi = (typeof import('mermaid'))['default'];

let loaded: Promise<MermaidApi> | undefined;
// Mermaid draws one diagram at a time (it uses one scratch element), so the diagrams of a note wait for each other.
let queue: Promise<unknown> = Promise.resolve();
let counter = 0;

function token(name: string, fallback: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

/** The colors of the application, so that a diagram looks like the page it is in. */
function themeVariables() {
  return {
    background: 'transparent',
    fontFamily: FONT,
    primaryColor: token('--paperCard', '#1d2245'),
    primaryTextColor: token('--ink', '#eef0ff'),
    primaryBorderColor: token('--hairlineStrong', '#3a4280'),
    secondaryColor: token('--paperWarm', '#161b3b'),
    tertiaryColor: token('--paperSunken', '#0d1026'),
    lineColor: token('--inkMuted', '#a9b1d6'),
    textColor: token('--ink', '#eef0ff'),
    noteBkgColor: token('--paperWarm', '#161b3b'),
    noteTextColor: token('--ink', '#eef0ff'),
    noteBorderColor: token('--accent', '#ffc145'),
    actorBkg: token('--paperCard', '#1d2245'),
    actorBorder: token('--hairlineStrong', '#3a4280'),
    actorTextColor: token('--ink', '#eef0ff'),
    signalColor: token('--inkSoft', '#c3c8ee'),
    signalTextColor: token('--ink', '#eef0ff'),
  };
}

function load(): Promise<MermaidApi> {
  loaded ??= import('mermaid').then(({ default: mermaid }) => {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: 'base',
      themeVariables: themeVariables(),
      fontFamily: FONT,
      maxTextSize: MAX_DIAGRAM_CHARS,
      htmlLabels: false,
      flowchart: { htmlLabels: false },
    });
    return mermaid;
  });
  return loaded;
}

/**
 * Gives the root of the SVG the size of its drawing: Mermaid sizes it to its container (`width="100%"`), and an image has no container
 * to ask. The page then scales it down to its column.
 */
function sized(svg: string): string {
  const document_ = new DOMParser().parseFromString(svg, 'image/svg+xml');
  const root = document_.documentElement;
  const box = root.getAttribute('viewBox')?.split(/\s+/).map(Number);
  if (root.localName !== 'svg' || box?.length !== 4 || box.some(Number.isNaN)) {
    throw new Error('The diagram could not be drawn.');
  }
  root.setAttribute('width', String(Math.ceil(box[2] ?? 0)));
  root.setAttribute('height', String(Math.ceil(box[3] ?? 0)));
  root.removeAttribute('style');
  return new XMLSerializer().serializeToString(root);
}

/** The address of an image that shows the diagram, or the reason it cannot be drawn (a syntax error is the usual one). */
export function drawDiagram(source: string): Promise<string> {
  if (source.length > MAX_DIAGRAM_CHARS) {
    return Promise.reject(new Error('The diagram is too large to be drawn.'));
  }
  const run = async () => {
    const mermaid = await load();
    counter++;
    const { svg } = await mermaid.render(`mermaid-diagram-${counter}`, source);
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(sized(svg))}`;
  };
  const result = queue.then(run, run);
  queue = result.catch(() => undefined);
  return result;
}
