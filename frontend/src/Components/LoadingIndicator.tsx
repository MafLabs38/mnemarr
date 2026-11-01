import translate from 'Utilities/String/translate';

export default function LoadingIndicator() {
  return <p role="status">{translate('Loading')}</p>;
}
