// The things that decide a version, tested without a network and without a token: the release rules, the version of an image and the
// guard on the seed tag. Run by the `release-tooling` job of the CI (`yarn test` in tools/release).
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
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
    ['feat(deps): x', 'minor'], // the scoped rule must not downgrade a feat
    ['build(deps): Bump a collector crate', null],
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
  const release = (messages) =>
    analyzeCommits(analyzer, { cwd: here, commits: messages.map((message, i) => ({ hash: String(i), message })), logger });
  assert.equal(await release(['docs: a', 'fix: b', 'feat: c', 'chore: d']), 'minor');
  assert.equal(await release(['fix: a', 'fix!: b']), 'minor'); // a breaking fix beside a fix
  assert.equal(await release(['docs: a', 'chore: b']), null);
});

test('semantic-release tells the next job the version it chose', () => {
  const exec = config.plugins.find((plugin) => Array.isArray(plugin) && plugin[0] === '@semantic-release/exec')[1];
  assert.match(exec.successCmd, /version=\$\{nextRelease\.version\}.*GITHUB_OUTPUT/);
});

test('the version of an image', async (t) => {
  const version = (env) =>
    execFileSync('bash', [path.join(scripts, 'version.sh')], { env: { PATH: process.env.PATH, ...env }, encoding: 'utf8' }).trim();

  await t.test('a release is X.Y.Z.<run>', () => assert.equal(version({ RELEASE: '0.1.0', RUN: '123', REF: 'main' }), '0.1.0.123'));
  await t.test('no release is 0.0.0-<ref>.<run>', () => assert.equal(version({ RELEASE: '', RUN: '7', REF: 'develop' }), '0.0.0-develop.7'));
  await t.test('a ref that is not a valid version part is cleaned', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: 'feature/3_x y' }), '0.0.0-feature-3-x-y.7'));
  await t.test('dots and dashes of a ref become dashes', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: 'feature/1.2-x' }), '0.0.0-feature-1-2-x.7'));
  await t.test('a missing run number is an error, not a version', () => {
    const result = spawnSync('bash', [path.join(scripts, 'version.sh')], { env: { PATH: process.env.PATH, RELEASE: '0.1.0' }, encoding: 'utf8' });
    assert.notEqual(result.status, 0);
  });
});

test('the guard on the seed tag', async (t) => {
  const guard = ({ cwd, env }) => spawnSync('bash', [path.join(scripts, 'require-release-tag.sh')], { cwd, env, encoding: 'utf8' });
  // A repository of its own, removed afterwards, and none of the developer's git configuration (signing, hooks) in the way.
  const repository = () => {
    const cwd = mkdtempSync(path.join(tmpdir(), 'release-guard-'));
    t.after(() => rmSync(cwd, { recursive: true, force: true }));
    const env = { PATH: process.env.PATH, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1' };
    const git = (...args) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.test', ...args], { cwd, env });
    git('init', '-q');
    git('commit', '-q', '--allow-empty', '-m', 'initial');
    return { cwd, git, env };
  };

  await t.test('refuses to run without a v* tag', () => {
    const result = guard(repository());
    assert.equal(result.status, 1);
    assert.match(result.stdout, /No release tag/);
  });
  await t.test('refuses a repository whose only tags are not release tags', () => {
    const repo = repository();
    repo.git('tag', '1.0');
    repo.git('tag', 'release-1');
    assert.equal(guard(repo).status, 1);
  });
  await t.test('runs with the seed tag', () => {
    const repo = repository();
    repo.git('tag', 'v0.0.0');
    assert.equal(guard(repo).status, 0);
  });
  await t.test('says that git failed when it is not in a repository', () => {
    const cwd = mkdtempSync(path.join(tmpdir(), 'release-guard-none-'));
    t.after(() => rmSync(cwd, { recursive: true, force: true }));
    const result = guard({ cwd, env: { PATH: process.env.PATH, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CEILING_DIRECTORIES: tmpdir() } });
    assert.notEqual(result.status, 0);
    assert.doesNotMatch(result.stdout, /No release tag/);
  });
});
