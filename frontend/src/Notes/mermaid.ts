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

/** The most edges a diagram may have. */
const MAX_EDGES = 300;

const FONT = 'trebuchet ms, verdana, arial, sans-serif';

type MermaidApi = (typeof import('mermaid'))['default'];

let loaded: Promise<MermaidApi> | undefined;
// Mermaid draws one diagram at a time (it uses one scratch element), so the diagrams of a note wait for each other.
let queue: Promise<unknown> = Promise.resolve();
let counter = 0;

// Mermaid cannot read a CSS variable, so the tokens are read here and given to it; the fallbacks are the values of the dark theme (themes.css).
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

/**
 * What a diagram cannot change. A diagram may carry a `%%{init: ...}%%` directive that Mermaid merges into its settings: spacing and
 * the like are welcome (notes commonly do), but the safety of the drawing and the look of the page are not the note's to set.
 * Setting `secure` replaces Mermaid's own list, so what it protects by default (`secure`, `securityLevel`, `startOnLoad`, `maxTextSize`,
 * `suppressErrorRendering`, `maxEdges`) is repeated here, with what we add.
 */
export const LOCKED_SETTINGS = [
  'secure',
  'securityLevel',
  'startOnLoad',
  'maxTextSize',
  'maxEdges',
  'suppressErrorRendering',
  'htmlLabels',
  'theme',
  'themeCSS',
  'themeVariables',
  'fontFamily',
  'dompurifyConfig',
];

function load(): Promise<MermaidApi> {
  loaded ??= import('mermaid').then(({ default: mermaid }) => mermaid);
  return loaded;
}

function settings() {
  return {
    startOnLoad: false,
    securityLevel: 'strict' as const,
    theme: 'base' as const,
    themeVariables: themeVariables(),
    fontFamily: FONT,
    maxTextSize: MAX_DIAGRAM_CHARS,
    maxEdges: MAX_EDGES,
    htmlLabels: false,
    flowchart: { htmlLabels: false },
    secure: LOCKED_SETTINGS,
  };
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
    // At each drawing, so that a diagram never starts from what the one before it was told to change.
    mermaid.initialize(settings());
    counter++;
    const { svg } = await mermaid.render(`mermaid-diagram-${counter}`, source);
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(sized(svg))}`;
  };
  const result = queue.then(run, run);
  queue = result.catch(() => undefined);
  return result;
}
