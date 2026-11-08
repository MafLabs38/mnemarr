import assert from 'node:assert/strict';
import test from 'node:test';

import { loadConfig } from '../src/config.ts';

const base = {
  MNEMARR_AGENT_SECRET: 's'.repeat(40),
  MNEMARR_MCP_URL: 'http://mnemarr.example:8383/mcp',
};

test('the models offered and the default one can be set, and are checked', () => {
  const config = loadConfig({
    ...base,
    MNEMARR_AGENT_MODELS: 'sonnet,opus:Opus Next',
    MNEMARR_AGENT_DEFAULT_MODEL: 'opus',
  });

  assert.deepEqual(config.models, [
    { id: 'sonnet', label: 'sonnet' },
    { id: 'opus', label: 'Opus Next' },
  ]);
  assert.equal(config.defaultModel, 'opus');
  assert.equal(loadConfig(base).models, undefined);
  assert.throws(() => loadConfig({ ...base, MNEMARR_AGENT_MODELS: 'Sonnet 4' }), /not a model name/);
  assert.throws(
    () => loadConfig({ ...base, MNEMARR_AGENT_MODELS: 'sonnet', MNEMARR_AGENT_DEFAULT_MODEL: 'opus' }),
    /not one of MNEMARR_AGENT_MODELS/,
  );
});

test('the number of kept conversations has a default and bounds', () => {
  assert.equal(loadConfig(base).maxConversations, 200);
  assert.equal(loadConfig({ ...base, MNEMARR_AGENT_MAX_CONVERSATIONS: '50' }).maxConversations, 50);
  assert.throws(() => loadConfig({ ...base, MNEMARR_AGENT_MAX_CONVERSATIONS: '0' }), /between 1 and 5000/);
});

test('a model behind an OpenAI-compatible API is a second agent, set by its address and its model names', () => {
  assert.equal(loadConfig(base).openai, undefined);

  const config = loadConfig({
    ...base,
    MNEMARR_AGENT_OPENAI_URL: 'http://ollama:11434/v1',
    MNEMARR_AGENT_OPENAI_MODELS: 'llama3.1,qwen2.5-coder:Qwen',
    MNEMARR_AGENT_OPENAI_NAME: 'Local',
  });

  assert.deepEqual(config.openai, {
    baseUrl: 'http://ollama:11434/v1',
    apiKey: undefined,
    name: 'Local',
    models: [
      { id: 'llama3.1', label: 'llama3.1' },
      { id: 'qwen2.5-coder', label: 'Qwen' },
    ],
    defaultModel: 'llama3.1',
  });
  assert.equal(
    loadConfig({
      ...base,
      MNEMARR_AGENT_OPENAI_URL: 'https://api.example/v1',
      MNEMARR_AGENT_OPENAI_MODELS: 'gpt-4.1',
      MNEMARR_AGENT_OPENAI_KEY: ' sk-x ',
    }).openai?.apiKey,
    'sk-x',
  );
});

test('an OpenAI-compatible address with no model names, a bad address, or a default that is not offered stops the start', () => {
  assert.throws(
    () => loadConfig({ ...base, MNEMARR_AGENT_OPENAI_URL: 'http://x/v1' }),
    /MNEMARR_AGENT_OPENAI_MODELS is required/,
  );
  assert.throws(
    () => loadConfig({ ...base, MNEMARR_AGENT_OPENAI_URL: 'ftp://x', MNEMARR_AGENT_OPENAI_MODELS: 'm' }),
    /http\(s\) address/,
  );
  assert.throws(
    () =>
      loadConfig({
        ...base,
        MNEMARR_AGENT_OPENAI_URL: 'http://x/v1',
        MNEMARR_AGENT_OPENAI_MODELS: 'm',
        MNEMARR_AGENT_OPENAI_DEFAULT_MODEL: 'other',
      }),
    /not one of MNEMARR_AGENT_OPENAI_MODELS/,
  );
  assert.throws(
    () => loadConfig({ ...base, MNEMARR_AGENT_OPENAI_URL: 'http://x/v1', MNEMARR_AGENT_OPENAI_MODELS: 'Bad Name' }),
    /not a model name/,
  );
});
