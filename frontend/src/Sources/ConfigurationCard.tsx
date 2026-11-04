import { type FormEvent, useId, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Button from 'Components/Form/Button';
import LoadingIndicator from 'Components/LoadingIndicator';
import { ApiError } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import { type ConfigPlan, useApplyConfig, useConfigDocument, useConfigStatus } from './sources';
import styles from './Sources.module.css';

function Plan({ plan }: Readonly<{ plan: ConfigPlan }>) {
  const changes = plan.items.filter((item) => item.action !== 'Keep');
  return (
    <>
      <p>{translate(plan.applied ? 'PlanApplied' : 'PlanPreview', { changes: changes.length })}</p>
      <ul className={styles.plan}>
        {changes.map((item) => (
          <li key={`${item.action}/${item.kind}/${item.name}`}>
            {translate('PlanItem', { action: translate(item.action), kind: item.kind, name: item.name })}
          </li>
        ))}
      </ul>
    </>
  );
}

/** Configuration as code: the state as a YAML document to keep in a repository, and a place to preview and apply one. */
export default function ConfigurationCard() {
  const current = useConfigDocument();
  const status = useConfigStatus();
  const fromFile = status.data?.file === true;
  const apply = useApplyConfig();
  const [yaml, setYaml] = useState('');
  const currentId = useId();
  const yamlId = useId();

  function submit(dryRun: boolean) {
    return (event: FormEvent) => {
      event.preventDefault();
      apply.mutate({ yaml, dryRun });
    };
  }

  return (
    <>
      <p className={styles.hint}>{translate('ConfigurationHelp')}</p>
      {fromFile && <p className={styles.hint}>{translate('ConfigurationFile')}</p>}
      {status.data?.error && <Alert>{translate('ConfigurationFileFailed', { reason: status.data.error })}</Alert>}
      <label htmlFor={currentId}>{translate('CurrentConfiguration')}</label>
      {current.isPending && <LoadingIndicator />}
      {current.isError && <Alert>{translate('ConfigurationError')}</Alert>}
      {current.data !== undefined && <textarea className={styles.yaml} id={currentId} readOnly value={current.data} />}
      <form onSubmit={submit(false)}>
        <label htmlFor={yamlId}>{translate('ConfigurationToApply')}</label>
        <textarea
          className={styles.yaml}
          id={yamlId}
          onChange={(event) => setYaml(event.target.value)}
          spellCheck={false}
          value={yaml}
        />
        <div className={styles.actions}>
          <Button disabled={apply.isPending || yaml.trim() === ''} onClick={submit(true)} type="button" variant="quiet">
            {translate('Preview')}
          </Button>
          <Button disabled={apply.isPending || fromFile || yaml.trim() === ''} type="submit">
            {translate('Apply')}
          </Button>
        </div>
      </form>
      {apply.error && (
        <Alert>
          {apply.error.message}
          {apply.error instanceof ApiError && apply.error.errors.length > 0 && (
            <ul>
              {apply.error.errors.map((error) => (
                <li key={error}>{error}</li>
              ))}
            </ul>
          )}
        </Alert>
      )}
      {apply.data && <Plan plan={apply.data} />}
    </>
  );
}
