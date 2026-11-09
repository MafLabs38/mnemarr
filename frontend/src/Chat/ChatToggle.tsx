import { icons } from 'Helpers/Props/icons';
import translate from 'Utilities/String/translate';

import { useChatStore } from './chatStore';
import styles from './ChatToggle.module.css';
import { useChatStatus } from './useChat';

/** The header icon that opens the chat panel (ADR 0035); absent when the chat is off (no MNEMARR__AGENT__URL). */
export default function ChatToggle() {
  const status = useChatStatus();
  const open = useChatStore((state) => state.open);
  const setOpen = useChatStore((state) => state.setOpen);

  if (!status.data?.enabled) {
    return null;
  }

  return (
    <button
      aria-controls="chat-panel"
      aria-expanded={open}
      aria-label={translate('Chat')}
      className={open ? `${styles.toggle} ${styles.active}` : styles.toggle}
      onClick={() => setOpen(!open)}
      title={open ? translate('CloseChat') : translate('OpenChat')}
      type="button"
    >
      <icons.CHAT aria-hidden="true" size={20} strokeWidth={2.25} />
    </button>
  );
}
