import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import TextField from 'Components/Form/TextField';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';
import type { TranslationKey } from 'Utilities/String/en';
import translate from 'Utilities/String/translate';

import { type TitleSource, useConversationTitle, useRewriteTitle, useSetTitle } from './conversationTitles';
import styles from './Notes.module.css';

const sourceText: Record<TitleSource, TranslationKey> = {
  'first-sentence': 'TitleFromFirstSentence',
  summary: 'TitleFromModel',
  person: 'TitleFromPerson',
};

/** Changing the title of a conversation is changing how it is called, not what was said: a person types one, or has the model write it again. */
function TitleForm({ path, current, onDone }: Readonly<{ path: string; current: string; onDone: () => void }>) {
  const [title, setTitle] = useState(current);
  const set = useSetTitle(path);

  function submit(event: FormEvent) {
    event.preventDefault();
    set.mutate(title, { onSuccess: onDone });
  }

  return (
    <form className={styles.titleForm} onSubmit={submit}>
      {set.error && <Alert>{set.error.message}</Alert>}
      <TextField
        label={translate('ConversationTitle')}
        onChange={(event) => setTitle(event.target.value)}
        required
        value={title}
      />
      <div className={styles.actions}>
        <Button disabled={set.isPending} type="submit">
          {translate('SaveTitle')}
        </Button>
        <Button onClick={onDone} variant="quiet">
          {translate('Cancel')}
        </Button>
      </div>
    </form>
  );
}

/** The title of a conversation, where it comes from, and what a person who may write conversations can do about it. */
export default function ConversationTitle({ path }: Readonly<{ path: string }>) {
  const { can } = usePermissions();
  const info = useConversationTitle(path);
  const rewrite = useRewriteTitle(path);
  const [editing, setEditing] = useState(false);
  if (!info.data) {
    return null;
  }
  const canWrite = can(permissions.conversationsWrite);
  return (
    <section aria-label={translate('TitleOfTheConversation')} className={styles.conversationTitle}>
      <p className={styles.hint}>
        {translate('ConversationTitle')}: <strong>{info.data.title}</strong> · {translate(sourceText[info.data.source])}
      </p>
      {rewrite.error && <Alert>{rewrite.error.message}</Alert>}
      {canWrite && editing && <TitleForm current={info.data.title} onDone={() => setEditing(false)} path={path} />}
      {canWrite && !editing && (
        <div className={styles.actions}>
          <Button onClick={() => setEditing(true)} variant="quiet">
            {translate('ChangeTitle')}
          </Button>
          {info.data.modelEnabled && (
            <Button disabled={rewrite.isPending} onClick={() => rewrite.mutate()} variant="quiet">
              {translate(rewrite.isPending ? 'WritingTitle' : 'RewriteTitle')}
            </Button>
          )}
        </div>
      )}
    </section>
  );
}
