import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

import styles from './ChatPanel.module.css';
import formatAge from './formatAge';
import type { ConversationSummary } from './typings';

interface ConversationListProps {
  conversations: ConversationSummary[];
  currentId: string | null;
  onOpen: (id: string) => void;
  onDelete: (id: string) => void;
}

/** Past conversations, latest first: open one to read it back and continue it (the CLI session is resumed). */
export default function ConversationList({
  conversations,
  currentId,
  onOpen,
  onDelete,
}: Readonly<ConversationListProps>) {
  if (conversations.length === 0) {
    return <p className={styles.historyEmpty}>{translate('ChatNoConversations')}</p>;
  }
  return (
    <ul aria-label={translate('ChatHistory')} className={styles.history}>
      {conversations.map((conversation) => {
        const title = conversation.title || translate('NewChat');
        return (
          <li
            className={conversation.id === currentId ? `${styles.historyItem} ${styles.current}` : styles.historyItem}
            key={conversation.id}
          >
            <button
              aria-current={conversation.id === currentId ? 'true' : undefined}
              className={styles.historyOpen}
              onClick={() => onOpen(conversation.id)}
              type="button"
            >
              <span className={styles.historyTitle}>{title}</span>
              <span className={styles.historyMeta}>
                {conversation.pendingPermissions > 0
                  ? translate('ChatWaitingApproval')
                  : `${conversation.model} · ${formatAge(conversation.updatedAt)}`}
              </span>
            </button>
            <button
              aria-label={translate('DeleteConversation', { title })}
              className={styles.iconButton}
              onClick={() => onDelete(conversation.id)}
              title={translate('Delete')}
              type="button"
            >
              <icons.DELETE aria-hidden="true" size={14} strokeWidth={2.25} />
            </button>
          </li>
        );
      })}
    </ul>
  );
}
