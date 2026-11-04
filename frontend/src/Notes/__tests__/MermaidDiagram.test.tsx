import { act, render, screen } from '@testing-library/react';

const drawing = vi.hoisted(() => ({ resolvers: new Map<string, (address: string) => void>() }));
vi.mock('Notes/mermaid', () => ({
  drawDiagram: (source: string) =>
    new Promise<string>((resolve) => {
      drawing.resolvers.set(source, resolve);
    }),
}));

import MermaidDiagram from 'Notes/MermaidDiagram';

describe('MermaidDiagram', () => {
  it('shows only the diagram that was asked for last when the text changes while the first is being drawn', async () => {
    const view = render(<MermaidDiagram source="first" />);
    view.rerender(<MermaidDiagram source="second" />);

    drawing.resolvers.get('second')?.('data:image/svg+xml,second');
    drawing.resolvers.get('first')?.('data:image/svg+xml,first');

    expect(await screen.findByRole('img', { name: 'Diagram' })).toHaveAttribute('src', 'data:image/svg+xml,second');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(screen.getByRole('img', { name: 'Diagram' })).toHaveAttribute('src', 'data:image/svg+xml,second');
  });
});
