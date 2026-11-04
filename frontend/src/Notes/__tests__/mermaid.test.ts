const mermaidMock = vi.hoisted(() => ({ initialize: vi.fn(), render: vi.fn() }));
vi.mock('mermaid', () => ({ default: mermaidMock }));

import { drawDiagram, LOCKED_SETTINGS, MAX_DIAGRAM_CHARS } from 'Notes/mermaid';

const svg =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120.4 60" width="100%" style="max-width: 120px"><g/></svg>';

describe('drawDiagram', () => {
  beforeEach(() => {
    mermaidMock.initialize.mockReset();
    mermaidMock.render.mockReset();
  });

  it('draws a diagram as the address of an SVG image with the size of its drawing', async () => {
    mermaidMock.render.mockResolvedValue({ svg });

    const address = await drawDiagram('flowchart TB\n  A --> B');

    expect(address.startsWith('data:image/svg+xml;charset=utf-8,')).toBe(true);
    const drawn = decodeURIComponent(address.slice(address.indexOf(',') + 1));
    expect(drawn).toContain('width="121"');
    expect(drawn).toContain('height="60"');
    expect(drawn).not.toContain('style=');
    expect(mermaidMock.render).toHaveBeenCalledWith(
      expect.stringMatching(/^mermaid-diagram-\d+$/),
      'flowchart TB\n  A --> B',
    );
  });

  it('asks Mermaid for its strictest mode, and locks what a diagram must not change, at every drawing', async () => {
    mermaidMock.render.mockResolvedValue({ svg });

    await drawDiagram('a');
    await drawDiagram('b');

    expect(mermaidMock.initialize).toHaveBeenCalledTimes(2);
    expect(mermaidMock.initialize.mock.calls[0]?.[0]).toMatchObject({
      securityLevel: 'strict',
      startOnLoad: false,
      htmlLabels: false,
    });
    expect(mermaidMock.initialize.mock.calls[0]?.[0].secure).toEqual(LOCKED_SETTINGS);
    expect(LOCKED_SETTINGS).toEqual(
      expect.arrayContaining(['securityLevel', 'themeCSS', 'themeVariables', 'dompurifyConfig', 'htmlLabels']),
    );
  });

  it('refuses a diagram that is too large without asking Mermaid', async () => {
    await expect(drawDiagram('x'.repeat(MAX_DIAGRAM_CHARS + 1))).rejects.toThrow('too large');

    expect(mermaidMock.render).not.toHaveBeenCalled();
  });

  it.each([
    ['not an SVG', '<html xmlns="http://www.w3.org/1999/xhtml"></html>'],
    ['no viewBox', '<svg xmlns="http://www.w3.org/2000/svg"></svg>'],
    ['a viewBox that is not numbers', '<svg xmlns="http://www.w3.org/2000/svg" viewBox="a b c d"></svg>'],
  ])('refuses what Mermaid returns when it is %s', async (_name, returned) => {
    mermaidMock.render.mockResolvedValue({ svg: returned });

    await expect(drawDiagram('flowchart TB')).rejects.toThrow();
  });

  it('draws the next diagram after one that failed, one at a time and in order', async () => {
    const order: string[] = [];
    mermaidMock.render.mockImplementation(async (_id: string, source: string) => {
      order.push(source);
      if (source === 'bad') {
        throw new Error('Parse error');
      }
      return { svg };
    });

    const first = drawDiagram('bad');
    const second = drawDiagram('good');

    await expect(first).rejects.toThrow('Parse error');
    await expect(second).resolves.toContain('data:image/svg+xml');
    expect(order).toEqual(['bad', 'good']);
  });
});
