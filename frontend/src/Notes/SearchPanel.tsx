import { type FormEvent, useState } from 'react';
import { Link } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import LoadingIndicator from 'Components/LoadingIndicator';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import translate from 'Utilities/String/translate';

import { noteAddress, useSearch, useSemanticSearchState } from './notes';
import styles from './Notes.module.css';

const matchLabels = { keywords: 'MatchKeywords', meaning: 'MatchMeaning', both: 'MatchBoth' } as const;

/** What the person should know about the search by meaning: that it works, that it is catching up, or why it does not. */
function MeaningState() {
  const { data } = useSemanticSearchState();
  if (!data || data.state === 'off' || data.state === 'starting') {
    return null;
  }
  if (data.state === 'failed') {
    return <Alert>{translate('MeaningFailed', { reason: data.reason ?? '' })}</Alert>;
  }
  return data.computed < data.passages ? (
    <p className={styles.meaningState}>
      {translate('MeaningCatchingUp', { computed: data.computed, passages: data.passages })}
    </p>
  ) : null;
}

function Results({ query }: Readonly<{ query: string }>) {
  const { data: hits, isFetching, isError } = useSearch(query);
  const { can } = usePermissions();

  if (isFetching) {
    return <LoadingIndicator />;
  }
  if (isError) {
    return <Alert>{translate('SearchError')}</Alert>;
  }
  if (!hits || hits.length === 0) {
    return <p>{translate('NoResults')}</p>;
  }
  return (
    <ul className={styles.results}>
      {hits.map((hit) => (
        <li key={`${hit.sourceId}/${hit.path}/${hit.heading}/${hit.snippet}`}>
          <div className={styles.hitTitle}>
            {can(permissions.notesRead) ? <Link to={noteAddress(hit)}>{hit.title}</Link> : hit.title}
          </div>
          <div className={styles.hitWhere}>
            {hit.sourceName} · {hit.path}
            {hit.heading && ` · ${hit.heading}`}
          </div>
          <p className={styles.snippet}>{hit.snippet}</p>
          <p className={styles.hitMatch}>{translate(matchLabels[hit.match])}</p>
        </li>
      ))}
    </ul>
  );
}

/** The search over the notes the person may read, by words and by meaning, with each hit leading to its note. */
export default function SearchPanel() {
  const [text, setText] = useState('');
  const [asked, setAsked] = useState('');

  function submit(event: FormEvent) {
    event.preventDefault();
    setAsked(text.trim());
  }

  return (
    <>
      <form className={styles.search} onSubmit={submit}>
        <TextField label={translate('SearchTheNotes')} onChange={(event) => setText(event.target.value)} value={text} />
        <Button type="submit">{translate('Search')}</Button>
      </form>
      <MeaningState />
      {asked && <Results query={asked} />}
    </>
  );
}
