import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import translate from 'Utilities/String/translate';

import SourceForm from './SourceForm';
import SourceList from './SourceList';
import type { SourceItem, SourceKindItem } from './sources';
import styles from './Sources.module.css';

/** The name a form proposes for the first source of a kind: the memory vault is one thing, and is called so. */
const suggestedNames: Partial<Record<SourceKindItem['kind'], string>> = { Memory: 'memory' };

/**
 * One kind of source, under the rule behind it. A kind the base requires is a slot to fill, with what is missing said at the top, and
 * it has no "add" once it is full; the others are lists that grow.
 */
export default function KindSection({
  kind,
  sources,
}: Readonly<{ kind: SourceKindItem; sources: ReadonlyArray<SourceItem> }>) {
  const mine = sources.filter((source) => source.kind === kind.kind);
  const full = kind.unique && mine.length >= 1;
  const title = translate(`${kind.kind}Title`);

  return (
    <Card title={title}>
      <p className={styles.hint}>{translate(`${kind.kind}Purpose`)}</p>
      {kind.required && <p>{translate(kind.satisfied ? 'Required' : 'RequiredMissing', { kind: title })}</p>}
      {!kind.satisfied && <Alert>{translate('SourceMissing', { kind: title })}</Alert>}
      {mine.length > 0 && <SourceList kind={kind} sources={mine} />}
      {!kind.declarable && mine.length === 0 && <p className={styles.hint}>{translate('ConversationsEmpty')}</p>}
      {kind.declarable && !full && <SourceForm kind={kind} suggestedName={suggestedNames[kind.kind]} />}
    </Card>
  );
}
