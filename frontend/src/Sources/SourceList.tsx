import { useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import SourceForm from './SourceForm';
import { type IndexReport, type SourceItem, type SourceKindItem, useDeleteSource, useIndexSource } from './sources';
import styles from './Sources.module.css';

function reportText(report: IndexReport): string {
  return translate('IndexReport', { added: report.added, updated: report.updated, removed: report.removed });
}

interface RowProps {
  source: SourceItem;
  kind: SourceKindItem;
  onResult: (error: Error | null) => void;
  onEdit: () => void;
}

function SourceRow({ source, kind, onResult, onEdit }: Readonly<RowProps>) {
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const index = useIndexSource(source.id);
  const remove = useDeleteSource(source.id);
  const outcome = { onSuccess: () => onResult(null), onError: onResult };
  // The base cannot be taken apart: the last source a kind requires can be changed, not removed.
  const removable = kind.count > (kind.required ? 1 : 0);

  return (
    <tr>
      <th scope="row">
        {source.name}
        {source.managed && <span className={styles.managed}>{translate('ManagedByConfig')}</span>}
      </th>
      <td>{translate(source.mode)}</td>
      <td className={styles.path}>{source.path}</td>
      <td>{source.notes}</td>
      <td>{source.chunks}</td>
      <td>
        {source.lastIndexedAt ? formatDateTime(source.lastIndexedAt) : translate('NeverIndexed')}
        {source.lastError && (
          <p className={styles.scanFailed} role="alert">
            {translate('ScanFailed', { reason: translate(source.lastError) })}
          </p>
        )}
      </td>
      <td>
        <div className={styles.actions}>
          <Button
            aria-label={translate('IndexNowFor', { name: source.name })}
            disabled={index.isPending}
            onClick={() => index.mutate(undefined, outcome)}
            variant="quiet"
          >
            {translate('IndexNow')}
          </Button>
          {!source.managed && (
            <Button aria-label={translate('ChangeSourceNamed', { name: source.name })} onClick={onEdit} variant="quiet">
              {translate('Change')}
            </Button>
          )}
          {removable && !source.managed && (
            <Button
              disabled={remove.isPending}
              onClick={() => (confirmingDelete ? remove.mutate(undefined, outcome) : setConfirmingDelete(true))}
              variant="danger"
            >
              {confirmingDelete ? translate('ConfirmDelete', { name: source.name }) : translate('Delete')}
            </Button>
          )}
          {confirmingDelete && (
            <Button onClick={() => setConfirmingDelete(false)} variant="quiet">
              {translate('Cancel')}
            </Button>
          )}
          {index.data && <output>{reportText(index.data)}</output>}
        </div>
      </td>
    </tr>
  );
}

/** The sources of one kind, with what was indexed and what can be done to each. */
export default function SourceList({
  kind,
  sources,
}: Readonly<{ kind: SourceKindItem; sources: ReadonlyArray<SourceItem> }>) {
  const [error, setError] = useState<Error | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const editedSource = sources.find((source) => source.id === editing);

  return (
    <>
      {error && <Alert>{error.message}</Alert>}
      <table className={styles.table}>
        <thead>
          <tr>
            <th scope="col">{translate('Name')}</th>
            <th scope="col">{translate('Mode')}</th>
            <th scope="col">{translate('Folder')}</th>
            <th scope="col">{translate('Notes')}</th>
            <th scope="col">{translate('Chunks')}</th>
            <th scope="col">{translate('LastIndexed')}</th>
            <th scope="col">{translate('Actions')}</th>
          </tr>
        </thead>
        <tbody>
          {sources.map((source) => (
            <SourceRow
              key={source.id}
              kind={kind}
              onEdit={() => setEditing(source.id)}
              onResult={setError}
              source={source}
            />
          ))}
        </tbody>
      </table>
      {editedSource && (
        <SourceForm key={editedSource.id} kind={kind} onDone={() => setEditing(null)} source={editedSource} />
      )}
    </>
  );
}
