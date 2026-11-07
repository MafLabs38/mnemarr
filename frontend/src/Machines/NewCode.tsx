import { useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import { copyText } from 'Utilities/Clipboard/copyText';
import translate from 'Utilities/String/translate';

import type { CreatedEnrolment, InstallLine } from './machines';
import styles from './Machines.module.css';

/** The system the person is probably on, so that the line to paste is the right one without asking. */
function likelySystem(lines: ReadonlyArray<InstallLine>): InstallLine['system'] | undefined {
  const onWindows = /win/i.test(navigator.userAgent);
  return (lines.find((line) => (line.system === 'windows') === onWindows) ?? lines[0])?.system;
}

/**
 * The line that installs the collector on the machine and joins it, shown once with its code: it is kept nowhere. When this Mnemarr has no
 * program to give, the command that only joins a machine that has it already.
 */
export default function NewCode({
  created,
  onDismiss,
}: Readonly<{ created: CreatedEnrolment; onDismiss: () => void }>) {
  const [copied, setCopied] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [system, setSystem] = useState(() => likelySystem(created.installs));
  const line = created.installs.find((candidate) => candidate.system === system);
  const text = line?.command ?? created.command;

  async function copy() {
    // When the page cannot copy at all, the line is selected with one click, and the person is told to copy it.
    const done = await copyText(text);
    setCopied(done);
    setCopyFailed(!done);
  }

  return (
    <section aria-label={translate('NewCode')} className={styles.newCode}>
      <p>{translate(line ? 'PasteThisLine' : 'NoProgram', { name: created.enrolment.machine })}</p>
      {created.installs.length > 1 && (
        <SelectField
          label={translate('System')}
          onChange={(event) => setSystem(event.target.value as InstallLine['system'])}
          options={created.installs.map((candidate) => ({ value: candidate.system, label: candidate.label }))}
          value={system}
        />
      )}
      {line?.setupUrl && (
        <div className={styles.download}>
          <a className={styles.downloadButton} download href={line.setupUrl}>
            {translate('DownloadInstaller', { system: line.label })}
          </a>
          <p className={styles.hint}>{translate('DownloadInstallerHelp')}</p>
        </div>
      )}
      {line?.downloadUrl && (
        <div className={styles.download}>
          <a className={styles.downloadButton} download href={line.downloadUrl}>
            {translate('DownloadFor', { system: line.label })}
          </a>
          <p className={styles.hint}>{translate('DownloadHelp')}</p>
          <p>{translate('OrPasteThisLine')}</p>
        </div>
      )}
      <code className={styles.secret}>{text}</code>
      {created.insecure && <Alert>{translate('InsecureWarning')}</Alert>}
      <p className={styles.hint}>{translate('NewCodeCode', { code: created.code })}</p>
      {copyFailed && <output>{translate('CopyLineUnavailable')}</output>}
      <div className={styles.inline}>
        <Button onClick={copy}>{translate(copied ? 'Copied' : 'CopyCommand')}</Button>
        <Button onClick={onDismiss} variant="quiet">
          {translate('Done')}
        </Button>
      </div>
    </section>
  );
}
