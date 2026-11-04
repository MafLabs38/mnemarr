import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import LoadingIndicator from 'Components/LoadingIndicator';
import translate from 'Utilities/String/translate';

import { useSearch } from './sources';
import styles from './Sources.module.css';

function Results({ query }: Readonly<{ query: string }>) {
  const { data: hits, isFetching, isError } = useSearch(query);

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
          <div className={styles.hitTitle}>{hit.title}</div>
          <div className={styles.hitWhere}>
            {hit.sourceName} · {hit.path}
            {hit.heading && ` · ${hit.heading}`}
          </div>
          <p className={styles.snippet}>{hit.snippet}</p>
        </li>
      ))}
    </ul>
  );
}

/** Tries the keyword search over the indexed notes, to see that what was declared is found. */
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
      {asked && <Results query={asked} />}
    </>
  );
}
