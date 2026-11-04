import { useEffect, useState } from 'react';

import translate from 'Utilities/String/translate';

import { drawDiagram } from './mermaid';
import styles from './Notes.module.css';

type Drawing = { image: string } | { error: string };

/** A ```mermaid block of a note, drawn as an image; when it cannot be, the reason and the text of the diagram. */
export default function MermaidDiagram({ source }: Readonly<{ source: string }>) {
  const [drawing, setDrawing] = useState<Drawing | null>(null);

  useEffect(() => {
    let current = true;
    drawDiagram(source).then(
      (image) => current && setDrawing({ image }),
      (error: unknown) => current && setDrawing({ error: error instanceof Error ? error.message : String(error) }),
    );
    return () => {
      current = false;
    };
  }, [source]);

  if (drawing === null) {
    return <p className={styles.hint}>{translate('DrawingDiagram')}</p>;
  }
  if ('error' in drawing) {
    return (
      <div className={styles.diagramError} role="alert">
        <p>{translate('DiagramError')}</p>
        <pre>{source}</pre>
      </div>
    );
  }
  return (
    <figure className={styles.diagram}>
      <img alt={translate('Diagram')} src={drawing.image} />
      <details>
        <summary>{translate('DiagramSource')}</summary>
        <pre>{source}</pre>
      </details>
    </figure>
  );
}
