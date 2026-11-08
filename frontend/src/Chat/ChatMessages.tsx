import { useEffect, useRef } from 'react';

import Alert from 'Components/Alert/Alert';
import translate from 'Utilities/String/translate';

import type { ChatItem } from './chatItems';
import ChatMarkdown from './ChatMarkdown';
import styles from './ChatMessages.module.css';
import PermissionCard from './PermissionCard';
import ToolCallBlock from './ToolCallBlock';
import UsageLine from './UsageLine';

interface ChatMessagesProps {
  items: ChatItem[];
  draft: string;
  running: boolean;
  answering: boolean;
  onAnswer: (requestId: string, approve: boolean) => void;
}

function Item({
  item,
  answering,
  onAnswer,
}: Readonly<{ item: ChatItem } & Pick<ChatMessagesProps, 'answering' | 'onAnswer'>>) {
  switch (item.kind) {
    case 'user':
      return <div className={styles.user}>{item.text}</div>;
    case 'text':
      return (
        <div className={styles.text}>
          <ChatMarkdown text={item.text} />
        </div>
      );
    case 'thinking':
      return (
        <details className={styles.thinking}>
          <summary>{translate('Thinking')}</summary>
          <p className={styles.thinkingText}>{item.text}</p>
        </details>
      );
    case 'tool':
      return <ToolCallBlock input={item.input} name={item.name} result={item.result} />;
    case 'permission':
      return (
        <PermissionCard
          busy={answering}
          input={item.input}
          onAnswer={(approve) => onAnswer(item.requestId, approve)}
          state={item.state}
          toolName={item.toolName}
        />
      );
    case 'turn-end':
      return item.usage ? <UsageLine usage={item.usage} /> : null;
    case 'error':
      return <Alert>{item.message}</Alert>;
    default:
      return null;
  }
}

/** The conversation, as an IDE chat shows it (D-90): messages, collapsible tool calls, permission cards, usage. */
export default function ChatMessages({ items, draft, running, answering, onAnswer }: Readonly<ChatMessagesProps>) {
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' });
  }, [items.length, draft]);

  return (
    <div
      aria-busy={running}
      aria-label={translate('Conversation')}
      aria-live="polite"
      className={styles.messages}
      role="log"
    >
      {items.length === 0 && !running ? <p className={styles.empty}>{translate('ChatEmpty')}</p> : null}
      {items.map((item) => (
        <Item answering={answering} item={item} key={item.key} onAnswer={onAnswer} />
      ))}
      {draft ? <div className={styles.draft}>{draft}</div> : null}
      {running && !draft ? <div className={styles.working}>{translate('ChatWorking')}</div> : null}
      <div ref={end} />
    </div>
  );
}
