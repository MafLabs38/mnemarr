import { type FormEvent, useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import Button from 'Components/Form/Button';
import SelectField from 'Components/Form/SelectField';
import TextField from 'Components/Form/TextField';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { formatDateTime } from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

import { type MachineKindName, machineNameFor, useMachines } from './machines';
import styles from './Machines.module.css';
import { type PairingItem, useApprovePairing, useLookupPairing, usePairings, useRejectPairing } from './pairings';

type Outcome = { kind: 'linked'; name: string } | { kind: 'rejected' };

/** What is said under the name field: the name that will be used when it is not what was typed, and a warning when it is a machine that exists already. */
function NameNotes({ name, typed, knownAs }: Readonly<{ name: string; typed: string; knownAs: string | null }>) {
  return (
    <>
      {name !== '' && name !== typed && <p className={styles.hint}>{translate('MachineNameUsed', { name })}</p>}
      <NameTaken knownAs={knownAs} name={name} />
    </>
  );
}

/** Says when the name chosen is a machine that exists already: approving under it replaces its key, and the collector that runs with the old key stops. */
function NameTaken({ name, knownAs }: Readonly<{ name: string; knownAs: string | null }>) {
  const machines = useMachines();
  const taken = machines.data?.some(
    (machine) => machine.machine === name && machine.kind !== null && machine.state !== 'refused',
  );
  return taken && name !== knownAs ? <Alert>{translate('LinkNameExists', { name })}</Alert> : null;
}

/** The person says who the computer is for this Mnemarr: the name it will have here and whether it is always on, or refuses it. */
function Answer({ pairing, onDone }: Readonly<{ pairing: PairingItem; onDone: (outcome: Outcome) => void }>) {
  // The code is typed from the screen of the computer, always, even when the person came by the link the computer opened: a link that anybody can make carries a
  // code too, and only reading the code on the computer proves that this request is the one in front of the person.
  const [code, setCode] = useState('');
  const [typed, setTyped] = useState(() => pairing.knownAs ?? machineNameFor(pairing.hostname));
  const [kind, setKind] = useState<MachineKindName>('intermittent');
  const approve = useApprovePairing();
  const reject = useRejectPairing();
  const name = machineNameFor(typed);
  const busy = approve.isPending || reject.isPending;
  const failure = approve.error ?? reject.error;

  function submit(event: FormEvent) {
    event.preventDefault();
    approve.mutate(
      { id: pairing.id, machine: name, kind, code },
      { onSuccess: () => onDone({ kind: 'linked', name }) },
    );
  }

  return (
    <form className={styles.form} onSubmit={submit}>
      <p>{translate('LinkIntro')}</p>
      <p>
        {translate('LinkHostname')}: <strong>{pairing.hostname}</strong>
      </p>
      {pairing.knownAs && <Alert>{translate('LinkKnownAs', { name: pairing.knownAs })}</Alert>}
      <p className={styles.hint}>{translate('PairingExpires', { time: formatDateTime(pairing.expiresAt) })}</p>
      {failure && <Alert>{failure.message}</Alert>}
      <TextField
        label={translate('LinkCodeOnComputer')}
        onChange={(event) => setCode(event.target.value)}
        required
        value={code}
      />
      <TextField
        label={translate('LinkName')}
        onChange={(event) => setTyped(event.target.value)}
        required
        value={typed}
      />
      <NameNotes knownAs={pairing.knownAs} name={name} typed={typed} />
      <p className={styles.hint}>{translate('LinkNameHelp')}</p>
      <SelectField
        label={translate('Kind')}
        onChange={(event) => setKind(event.target.value as MachineKindName)}
        options={[
          { value: 'intermittent', label: translate('KindIntermittent') },
          { value: 'always-on', label: translate('KindAlwaysOn') },
        ]}
        value={kind}
      />
      <div className={styles.inline}>
        <Button disabled={busy || name === '' || code.trim() === ''} type="submit">
          {translate('LinkApprove')}
        </Button>
        <Button
          disabled={busy}
          onClick={() => reject.mutate(pairing.id, { onSuccess: () => onDone({ kind: 'rejected' }) })}
          variant="danger"
        >
          {translate('LinkReject')}
        </Button>
      </div>
    </form>
  );
}

/** Finds the request by the code the computer shows, or by the request itself when a person came from the list. */
function useWantedPairing(code: string, id: string | null) {
  const lookup = useLookupPairing();
  const listed = usePairings(id !== null);
  const asked = useRef('');
  useEffect(() => {
    if (id === null && code !== '' && asked.current !== code) {
      asked.current = code;
      lookup.mutate(code);
    }
  }, [code, id, lookup]);
  if (id !== null) {
    const found = listed.data?.find((item) => item.id === id);
    return { pairing: found, notFound: listed.isSuccess && !found, loading: listed.isPending };
  }
  return { pairing: lookup.data, notFound: lookup.isError, loading: lookup.isPending };
}

/**
 * Where a person answers a computer that asked to be linked (ADR 0030): the collector opens this page with the short code it shows, or a person comes
 * from the Machines page. A person always decides, and names the machine.
 */
export default function LinkMachine() {
  const [params, setParams] = useSearchParams();
  const code = params.get('code') ?? '';
  const id = params.get('id');
  const [typed, setTyped] = useState(code);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const wanted = useWantedPairing(code, id);

  return (
    <PageContent title={translate('LinkAComputer')}>
      <PageContentBody>
        <PageHeading scope={translate('Machines')} title={translate('LinkAComputer')} />
        {outcome ? (
          <Card title={translate('LinkAComputer')}>
            <output>
              {outcome.kind === 'linked' ? translate('LinkDone', { name: outcome.name }) : translate('LinkRejected')}
            </output>
            <p>
              <Link to="/machines">{translate('LinkSeeMachines')}</Link>
            </p>
          </Card>
        ) : (
          <Card title={translate('LinkAComputer')}>
            {code === '' && id === null && (
              <form
                className={styles.form}
                onSubmit={(event) => {
                  event.preventDefault();
                  setParams({ code: typed.trim() });
                }}
              >
                <TextField
                  label={translate('LinkCodeLabel')}
                  onChange={(event) => setTyped(event.target.value)}
                  required
                  value={typed}
                />
                <Button disabled={typed.trim() === ''} type="submit">
                  {translate('LinkFind')}
                </Button>
              </form>
            )}
            {wanted.notFound && <Alert>{translate('LinkNotFound')}</Alert>}
            {wanted.pairing && <Answer onDone={setOutcome} pairing={wanted.pairing} />}
          </Card>
        )}
      </PageContentBody>
    </PageContent>
  );
}
