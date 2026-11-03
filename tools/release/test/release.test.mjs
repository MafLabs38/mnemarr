// The things that decide a version, tested without a network and without a token: the release rules, the version of an image and the
// guard on the seed tag. Run by the `release-tooling` job of the CI (`yarn test` in tools/release).
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { analyzeCommits } from '@semantic-release/commit-analyzer';

const here = path.dirname(fileURLToPath(import.meta.url));
const scripts = path.join(here, '..', 'scripts');
const config = JSON.parse(readFileSync(path.join(here, '..', '.releaserc.json'), 'utf8'));
const analyzer = config.plugins.find((plugin) => Array.isArray(plugin) && plugin[0] === '@semantic-release/commit-analyzer')[1];
const logger = { log() {}, error() {} };

const releaseOf = (message) => analyzeCommits(analyzer, { cwd: here, commits: [{ hash: 'abc', message }], logger });

test('the release rules', async (t) => {
  const cases = [
    ['feat(api): add the status endpoint', 'minor'],
    ['fix(search): rank by date', 'patch'],
    ['perf: faster index', 'patch'],
    ['revert: undo the change', 'patch'],
    ['feat(api)!: rename a field', 'minor'], // below 1.0 a breaking change is a minor
    ['fix!: change a default', 'minor'],
    ['feat: x\n\nBREAKING CHANGE: the field is gone', 'minor'],
    ['chore(deps): Bump a from 1 to 2', 'patch'], // a dependency of the image is a patch: a fixed vulnerability must be released
    ['chore(deps-dev): Bump a from 1 to 2', null],
    ['ci(deps): Bump an action', null],
    ['docs(adr): explain', null],
    ['docs!: rewrite', null], // a breaking change of a type that does not release does not release
    ['ci!: change the pipeline', null],
    ['chore: tidy', null],
    ['test: add a case', null],
    ['refactor: rename', null],
    ['style: format', null],
    ['build(docker): pin', null],
    ["Merge pull request #1 from x/y\n\nRelease", null],
  ];
  for (const [message, expected] of cases) {
    await t.test(`${JSON.stringify(message.split('\n')[0])} releases ${expected ?? 'nothing'}`, async () => {
      assert.equal(await releaseOf(message), expected);
    });
  }
});

test('the highest release of several commits wins', async () => {
  const commits = ['docs: a', 'fix: b', 'feat: c', 'chore: d'].map((message, i) => ({ hash: String(i), message }));
  assert.equal(await analyzeCommits(analyzer, { cwd: here, commits, logger }), 'minor');
});

test('the version of an image', async (t) => {
  const version = (env) =>
    execFileSync('bash', [path.join(scripts, 'version.sh')], { env: { PATH: process.env.PATH, ...env }, encoding: 'utf8' }).trim();

  await t.test('a release is X.Y.Z.<run>', () => assert.equal(version({ RELEASE: '0.1.0', RUN: '123', REF: 'main' }), '0.1.0.123'));
  await t.test('no release is 0.0.0-<ref>.<run>', () => assert.equal(version({ RELEASE: '', RUN: '7', REF: 'develop' }), '0.0.0-develop.7'));
  await t.test('a ref that is not a valid version part is cleaned', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: 'feature/3_x y' }), '0.0.0-feature-3-x-y.7'));
});

test('the guard on the seed tag', async (t) => {
  const guard = (cwd) => spawnSync('bash', [path.join(scripts, 'require-release-tag.sh')], { cwd, encoding: 'utf8' });
  const repository = () => {
    const cwd = mkdtempSync(path.join(tmpdir(), 'release-guard-'));
    const git = (...args) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.test', ...args], { cwd });
    git('init', '-q');
    git('commit', '-q', '--allow-empty', '-m', 'initial');
    return { cwd, git };
  };

  await t.test('refuses to run without a v* tag', () => {
    const { cwd } = repository();
    const result = guard(cwd);
    assert.equal(result.status, 1);
    assert.match(result.stdout, /No release tag/);
  });
  await t.test('runs with the seed tag', () => {
    const { cwd, git } = repository();
    git('tag', 'v0.0.0');
    assert.equal(guard(cwd).status, 0);
  });
});
