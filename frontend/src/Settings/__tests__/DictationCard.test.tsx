import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders, sentBody, stubApi } from '__tests__/render';
import type { TranscriptionSettings } from 'Settings/DictationCard';
import Settings from 'Settings/Settings';

const on: TranscriptionSettings = {
  enabled: true,
  provider: 'OpenAiCompatible',
  destination: 'api.openai.com',
  models: [
    { id: 'gpt-transcribe', label: 'GPT Transcribe', pricePerMinute: 0.0045 },
    { id: 'whisper-local', label: 'Whisper on the GPU', pricePerMinute: null },
  ],
  modelsFromFile: false,
  modelsProblem: null,
  model: 'gpt-transcribe',
  modelLayer: 'catalog',
  modelProblem: null,
  language: 'fr',
  maxMegabytes: 10,
  maxPerPersonPerDay: 100,
};

function serve(settings: TranscriptionSettings) {
  return stubApi((method, path, init) => {
    if (path === '/transcription') {
      return { body: settings };
    }
    if (method === 'PUT' && path === '/transcription/model') {
      const { model } = JSON.parse(init?.body as string) as { model: string | null };
      return { body: { ...settings, model: model ?? 'gpt-transcribe', modelLayer: model ? 'interface' : 'catalog' } };
    }
    return undefined;
  });
}

async function card() {
  await screen.findByText(/Dictation is off|what a person dictates/);
  return screen.getByRole('region', { name: 'Dictation' });
}

describe('DictationCard', () => {
  it('says that dictation is off and what turning it on would send where', async () => {
    serve({ ...on, enabled: false });
    renderWithProviders(<Settings />, '/settings');

    const dictation = await card();

    expect(within(dictation).getByText(/Dictation is off/)).toBeInTheDocument();
    expect(within(dictation).queryByLabelText('Model')).not.toBeInTheDocument();
  });

  it('says where the sound goes, and offers the models of the catalog with their price', async () => {
    serve(on);
    renderWithProviders(<Settings />, '/settings');

    const dictation = await card();

    expect(within(dictation).getByText(/sent to api\.openai\.com/)).toBeInTheDocument();
    expect(within(dictation).getByRole('option', { name: 'GPT Transcribe ($0.0045 a minute)' })).toBeInTheDocument();
    expect(within(dictation).getByRole('option', { name: 'Whisper on the GPU' })).toBeInTheDocument();
    expect(within(dictation).getByLabelText('Model')).toHaveValue('');
    expect(within(dictation).getByText(/In use: gpt-transcribe \(the first of the catalog\)/)).toBeInTheDocument();
  });

  it('chooses a model, and gives the choice back to the deployment', async () => {
    const fetchMock = serve(on);
    renderWithProviders(<Settings />, '/settings');
    const dictation = await card();

    await userEvent.selectOptions(within(dictation).getByLabelText('Model'), 'whisper-local');

    await waitFor(() => expect(within(dictation).getByLabelText('Model')).toHaveValue('whisper-local'));
    const put = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/v1/transcription/model' && init?.method === 'PUT',
    );
    expect(put && sentBody(put)).toEqual({ model: 'whisper-local' });
  });

  it('shows why the file of the models or the model asked for is not used', async () => {
    serve({
      ...on,
      modelsProblem: 'The file of the transcription models cannot be used.',
      modelProblem: "The model 'old' is not in the catalog any more.",
    });
    renderWithProviders(<Settings />, '/settings');

    const dictation = await card();

    expect(within(dictation).getByText('The file of the transcription models cannot be used.')).toBeInTheDocument();
    expect(within(dictation).getByText("The model 'old' is not in the catalog any more.")).toBeInTheDocument();
  });
});
