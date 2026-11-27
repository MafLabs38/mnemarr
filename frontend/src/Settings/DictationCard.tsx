import { useMutation, useQueryClient } from '@tanstack/react-query';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import SelectField from 'Components/Form/SelectField';
import { apiSend } from 'Helpers/Api/apiGet';
import { useApiQuery } from 'Helpers/Hooks/useApiQuery';
import translate from 'Utilities/String/translate';

import styles from './Settings.module.css';

interface TranscriptionModel {
  id: string;
  label: string;
  /** Dollars per minute of sound, when the catalog says. */
  pricePerMinute: number | null;
}

/** The dictation as an administrator sees it (ADR 0047). */
export interface TranscriptionSettings {
  enabled: boolean;
  provider: string | null;
  /** The host the recordings are sent to. */
  destination: string | null;
  models: TranscriptionModel[];
  modelsFromFile: boolean;
  modelsProblem: string | null;
  model: string;
  modelLayer: 'interface' | 'deployment' | 'catalog';
  modelProblem: string | null;
  language: string | null;
  maxMegabytes: number;
  maxPerPersonPerDay: number;
}

const path = '/transcription';

/** The model's label with its price, so that the choice says what it costs. */
function optionLabel(model: TranscriptionModel): string {
  return model.pricePerMinute === null
    ? model.label
    : translate('DictationModelPrice', { label: model.label, price: model.pricePerMinute.toString() });
}

/**
 * The chat's dictation (ADR 0047): whether it is on and where the sound goes, the models of the catalog (the deployment's file, or Mnemarr's own list)
 * with their price, and the model in use. Choosing one here wins over the deployment's; "the deployment's" gives it back.
 */
export default function DictationCard() {
  const client = useQueryClient();
  const { data, error } = useApiQuery<TranscriptionSettings>(path);
  const choose = useMutation({
    mutationFn: (model: string | null) => apiSend<TranscriptionSettings>('PUT', `${path}/model`, { model }),
    gcTime: 0,
    onSuccess: (settings) => client.setQueryData(['api', path], settings),
  });

  if (!data) {
    return <Card title={translate('Dictation')}>{error ? <Alert>{error.message}</Alert> : translate('Loading')}</Card>;
  }

  if (!data.enabled) {
    return (
      <Card title={translate('Dictation')}>
        <p className={styles.help}>{translate('DictationOff')}</p>
      </Card>
    );
  }

  const deployment = '';
  const options = [
    { value: deployment, label: translate('DictationModelOfDeployment') },
    ...data.models.map((model) => ({ value: model.id, label: optionLabel(model) })),
  ];

  return (
    <Card title={translate('Dictation')}>
      <p className={styles.help}>
        {translate('DictationOn', {
          provider: data.provider ?? '',
          destination: data.destination ?? '',
          max: data.maxPerPersonPerDay.toString(),
        })}
      </p>
      {data.modelsProblem && <Alert>{data.modelsProblem}</Alert>}
      {data.modelProblem && <Alert>{data.modelProblem}</Alert>}
      <div className={styles.form}>
        <SelectField
          disabled={choose.isPending}
          label={translate('DictationModel')}
          onChange={(event) => choose.mutate(event.target.value === deployment ? null : event.target.value)}
          options={options}
          value={data.modelLayer === 'interface' ? data.model : deployment}
        />
        <p className={styles.help}>
          {translate(data.modelsFromFile ? 'DictationCatalogFromFile' : 'DictationCatalogBuiltIn')}{' '}
          {translate('DictationModelInUse', {
            model: data.model,
            layer: translate(`DictationLayer_${data.modelLayer}`),
          })}
        </p>
        {choose.error && <Alert>{choose.error.message}</Alert>}
      </div>
    </Card>
  );
}
