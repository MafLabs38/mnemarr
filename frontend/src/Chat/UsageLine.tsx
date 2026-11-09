import translate from 'Utilities/String/translate';

import styles from './ChatMessages.module.css';
import type { Usage } from './typings';

const numberFormat = new Intl.NumberFormat('en');

/** Tokens, cost and duration of a turn, as the CLI reports them (a subscription reports a notional cost). */
export default function UsageLine({ usage }: Readonly<{ usage: Usage }>) {
  const parts = [
    translate('UsageTokens', {
      input: numberFormat.format(usage.inputTokens + usage.cacheReadTokens + usage.cacheWriteTokens),
      output: numberFormat.format(usage.outputTokens),
    }),
  ];
  if (usage.costUsd !== undefined) {
    parts.push(translate('UsageCost', { cost: usage.costUsd.toFixed(usage.costUsd < 0.1 ? 4 : 2) }));
  }
  if (usage.durationMs !== undefined) {
    parts.push(translate('UsageDuration', { seconds: (usage.durationMs / 1000).toFixed(1) }));
  }
  return <div className={styles.usage}>{parts.join(' · ')}</div>;
}
