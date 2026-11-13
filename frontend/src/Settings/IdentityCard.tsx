import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import Button from 'Components/Form/Button';
import fieldStyles from 'Components/Form/Field.module.css';
import TextField from 'Components/Form/TextField';
import { useIdentity, useRemoveLogo, useSetIdentity, useUploadLogo } from 'Identity/identity';
import translate from 'Utilities/String/translate';

import styles from './Settings.module.css';

const ACCEPTED_LOGOS = '.png,.webp,.svg,image/png,image/webp,image/svg+xml';

/**
 * The identity of this Mnemarr (ADR 0044): the name and the two colors of the badge at the bottom of the menu, and its logo. The server checks everything
 * (the name is one line, a color is a hex or a theme token, the image is read by its content), and says why when it refuses.
 */
export default function IdentityCard() {
  const { data } = useIdentity();
  const setIdentity = useSetIdentity();
  const upload = useUploadLogo();
  const remove = useRemoveLogo();
  const [fileKey, setFileKey] = useState(0);
  const [name, setName] = useState<string | null>(null);
  const [nameColor, setNameColor] = useState<string | null>(null);
  const [versionColor, setVersionColor] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [saved, setSaved] = useState(false);

  if (!data) {
    return <Card title={translate('Identity')}>{translate('Loading')}</Card>;
  }

  const currentName = name ?? data.name;
  const currentNameColor = nameColor ?? data.colors.name;
  const currentVersionColor = versionColor ?? data.colors.version;
  const textsChanged =
    currentName.trim() !== data.name ||
    currentNameColor.trim() !== data.colors.name ||
    currentVersionColor.trim() !== data.colors.version;
  const pending = setIdentity.isPending || upload.isPending || remove.isPending;
  const error = setIdentity.error ?? upload.error ?? remove.error;

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    // What went wrong the last time is not what is shown for this time.
    setIdentity.reset();
    upload.reset();
    remove.reset();
    try {
      if (textsChanged) {
        // Only what was changed: the form shows what applies, which may come from the deployment, and what was not touched must stay the deployment's.
        const colors = {
          ...(currentNameColor.trim() !== data.colors.name && { name: currentNameColor.trim() }),
          ...(currentVersionColor.trim() !== data.colors.version && { version: currentVersionColor.trim() }),
        };
        await setIdentity.mutateAsync({
          ...(currentName.trim() !== data.name && { name: currentName.trim() }),
          ...(Object.keys(colors).length > 0 && { colors }),
        });
      }
      if (file) {
        await upload.mutateAsync(file);
        setFile(null);
        // A new input, so that the file that was sent is not shown as still chosen.
        setFileKey((key) => key + 1);
      }
      setName(null);
      setNameColor(null);
      setVersionColor(null);
      setSaved(true);
    } catch {
      // The reason is shown from the mutation that failed.
    }
  };

  return (
    <Card title={translate('Identity')}>
      <p className={styles.help}>{translate('IdentityHelpText')}</p>
      {data.logoProblem && <Alert>{translate('IdentityLogoNotUsed', { reason: data.logoProblem })}</Alert>}
      <form className={styles.form} onSubmit={onSubmit}>
        <TextField
          label={translate('IdentityName')}
          maxLength={40}
          onChange={(event) => {
            setSaved(false);
            setName(event.target.value);
          }}
          value={currentName}
        />
        <TextField
          autoCapitalize="off"
          label={translate('IdentityNameColor')}
          maxLength={12}
          onChange={(event) => {
            setSaved(false);
            setNameColor(event.target.value);
          }}
          spellCheck={false}
          value={currentNameColor}
        />
        <TextField
          autoCapitalize="off"
          label={translate('IdentityVersionColor')}
          maxLength={12}
          onChange={(event) => {
            setSaved(false);
            setVersionColor(event.target.value);
          }}
          spellCheck={false}
          value={currentVersionColor}
        />
        <p className={styles.help}>{translate('IdentityColorHelp')}</p>
        <label className={fieldStyles.field}>
          <span className={fieldStyles.label}>{translate('IdentityLogo')}</span>
          <input
            accept={ACCEPTED_LOGOS}
            className={fieldStyles.control}
            key={fileKey}
            onChange={(event) => {
              setSaved(false);
              setFile(event.target.files?.[0] ?? null);
            }}
            type="file"
          />
        </label>
        <p className={styles.help}>{translate('IdentityLogoHelp')}</p>
        {error && <Alert>{error.message}</Alert>}
        {saved && <output>{translate('IdentitySaved')}</output>}
        <div className={styles.actions}>
          <Button disabled={pending || (!textsChanged && file === null)} type="submit">
            {translate('IdentitySave')}
          </Button>
          {data.logoUrl && (
            <Button
              disabled={pending}
              onClick={() => {
                setSaved(false);
                setIdentity.reset();
                upload.reset();
                remove.mutate(undefined);
              }}
              variant="quiet"
            >
              {translate('IdentityRemoveLogo')}
            </Button>
          )}
        </div>
      </form>
    </Card>
  );
}
