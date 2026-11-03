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
    ['revert!: undo with a new contract', 'minor'],
    ['feat: x\n\nBREAKING CHANGE: the field is gone', 'minor'],
    ['chore(deps): Bump a from 1 to 2', 'patch'], // a dependency of the image is a patch: a fixed vulnerability must be released
    ['feat(deps): x', 'minor'], // the scoped rule must not downgrade a feat
    ['build(deps): Bump a collector crate', null],
    ['chore(deps-dev): Bump a from 1 to 2', null],
    ['ci(deps): Bump an action', null],
    ['docs(adr): explain', null],
    ['docs!: rewrite', 'minor'], // a breaking change releases a minor whatever its type: it must not ride in a patch
    ['docs: x\n\nBREAKING CHANGE: the field is gone', 'minor'],
    ['chore(deps)!: Bump a to a breaking version', 'minor'],
    ['chore(deps): x\n\nBREAKING CHANGE: y', 'minor'],
    ['fix: x\n\nBREAKING-CHANGE: y', 'minor'], // the hyphen form of the footer
    ['fix(deps): x', 'patch'],
    ['chore: tidy', null],
    ['test: add a case', null],
    ['refactor: rename', null],
    ['style: format', null],
    ['feat(api): add the status endpoint (#12)\n\n* the endpoint\n* its tests', 'minor'], // a squash commit: the title of the pull request and a body
    ['Revert "feat: x"', null], // git's default message is not a Conventional Commit: the pull request is retitled `revert: …`
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
  assert.equal(await release(['fix: a', 'refactor!: b']), 'minor'); // a breaking refactor beside a fix is not a patch
});

test('semantic-release tells the next job the version it chose', () => {
  const exec = config.plugins.find((plugin) => Array.isArray(plugin) && plugin[0] === '@semantic-release/exec')[1];
  const directory = mkdtempSync(path.join(tmpdir(), 'release-output-'));
  try {
    const output = path.join(directory, 'github-output');
    // The command is a template of semantic-release: its only placeholder is the version it chose.
    const command = exec.successCmd.replaceAll('${nextRelease.version}', '0.1.0');
    assert.doesNotMatch(command, /nextRelease/);
    execFileSync('bash', ['-c', command], { env: { PATH: process.env.PATH, GITHUB_OUTPUT: output } });
    assert.equal(readFileSync(output, 'utf8'), 'version=0.1.0\n');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('the jobs that release and publish have the same gates', () => {
  // A gate added to one and not to the other would let an image out without it (the `image` job also waits for `release`).
  const workflow = readFileSync(path.join(here, '..', '..', '..', '.github', 'workflows', 'ci.yml'), 'utf8');
  const job = (name) => {
    const section = workflow.split(new RegExp(`^  ${name}:$`, 'm'))[1];
    assert.ok(section, `the job ${name} is not in ci.yml`);
    return section.split(/^  [a-z0-9_-]+:$/m)[0];
  };
  const needs = (name) => {
    const match = job(name).match(/^    needs: \[(.*)\]$/m);
    assert.ok(match, `the needs of ${name} must be one inline list: [a, b]`);
    return match[1].split(',').map((gate) => gate.trim()).sort();
  };
  assert.deepEqual(needs('image').filter((gate) => gate !== 'release'), needs('release'));

  // The conditions themselves: the tag needs the end-to-end test and the Sonar analysis to have passed, the image needs a release on main.
  const condition = (name) => job(name).match(/^    if: >-\n((?:      .*\n)+)/m)?.[1].replaceAll(/\s+/g, ' ');
  assert.match(condition('release'), /needs\.e2e-smoke\.result == 'success'/);
  assert.match(condition('release'), /needs\.sonarqube\.result == 'success'/);
  assert.match(condition('image'), /needs\.e2e-smoke\.result == 'success'/);
  assert.match(condition('image'), /needs\.release\.result == 'success'/);
  // An image leaves main only after a release; off main a skipped release is how develop publishes.
  assert.match(condition('image'), /github\.ref != 'refs\/heads\/main' && needs\.release\.result == 'skipped'/);
  assert.match(condition('release'), /github\.ref == 'refs\/heads\/main'/);
  assert.match(condition('release'), /!cancelled\(\)/);
});

test('the title check and the release rules know the same types', () => {
  const title = readFileSync(path.join(here, '..', '..', '..', '.github', 'workflows', 'pr-title.yml'), 'utf8');
  const fromTitle = title.match(/pattern='\^\(([a-z|]+)\)/)[1].split('|').sort();
  const fromRules = [...new Set(analyzer.releaseRules.map((rule) => rule.type).filter(Boolean))].sort();
  assert.deepEqual(fromTitle, fromRules);
});

test('the version of an image', async (t) => {
  const version = (env) =>
    execFileSync('bash', [path.join(scripts, 'version.sh')], { env: { PATH: process.env.PATH, ...env }, encoding: 'utf8' }).trim();

  await t.test('an unset RELEASE is no release', () => assert.equal(version({ RUN: '7', REF: 'develop' }), '0.0.0-develop.7'));
  await t.test('a release is X.Y.Z.<run>', () => assert.equal(version({ RELEASE: '0.1.0', RUN: '123', REF: 'main' }), '0.1.0.123'));
  await t.test('no release is 0.0.0-<ref>.<run>', () => assert.equal(version({ RELEASE: '', RUN: '7', REF: 'develop' }), '0.0.0-develop.7'));
  await t.test('a ref that is not a valid version part is cleaned', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: 'feature/3_x y' }), '0.0.0-feature-3-x-y.7'));
  await t.test('a ref of digits only gets a letter, a leading zero is not valid in a pre-release', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: '007' }), '0.0.0-r007.7'));
  await t.test('dots and dashes of a ref become dashes', () =>
    assert.equal(version({ RELEASE: '', RUN: '7', REF: 'feature/1.2-x' }), '0.0.0-feature-1-2-x.7'));
  for (const [name, env] of [
    ['a missing run number', { RELEASE: '0.1.0' }],
    ['an empty run number', { RELEASE: '0.1.0', RUN: '' }],
    ['a missing ref', { RELEASE: '', RUN: '7' }],
    ['an empty ref', { RELEASE: '', RUN: '7', REF: '' }],
  ]) {
    await t.test(`${name} is an error, not a version`, () => {
      const result = spawnSync('bash', [path.join(scripts, 'version.sh')], { env: { PATH: process.env.PATH, ...env }, encoding: 'utf8' });
      assert.notEqual(result.status, 0);
    });
  }
});

test('the guard on the seed tag', async (t) => {
  const guard = ({ cwd, env }) => spawnSync('bash', [path.join(scripts, 'require-release-tag.sh')], { cwd, env, encoding: 'utf8' });
  // A repository of its own, removed afterwards, and none of the developer's git configuration (signing, hooks) in the way.
  const repository = (subtest) => {
    const cwd = mkdtempSync(path.join(tmpdir(), 'release-guard-'));
    subtest.after(() => rmSync(cwd, { recursive: true, force: true }));
    const env = { PATH: process.env.PATH, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1' };
    const git = (...args) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.test', ...args], { cwd, env });
    git('init', '-q');
    git('commit', '-q', '--allow-empty', '-m', 'initial');
    return { cwd, git, env };
  };

  await t.test('refuses to run without a v* tag', (subtest) => {
    const result = guard(repository(subtest));
    assert.equal(result.status, 1);
    assert.match(result.stdout, /No release tag/);
  });
  await t.test('refuses a repository whose only tags are not release tags', (subtest) => {
    const repo = repository(subtest);
    repo.git('tag', '1.0');
    repo.git('tag', 'release-1');
    assert.equal(guard(repo).status, 1);
  });
  await t.test('takes any v* tag: semantic-release ignores the ones that are not versions', (subtest) => {
    const repo = repository(subtest);
    repo.git('tag', 'vNext');
    assert.equal(guard(repo).status, 0);
  });
  await t.test('runs with the seed tag', (subtest) => {
    const repo = repository(subtest);
    repo.git('tag', 'v0.0.0');
    assert.equal(guard(repo).status, 0);
  });
  await t.test('says that git failed when it is not in a repository', (subtest) => {
    const cwd = mkdtempSync(path.join(tmpdir(), 'release-guard-none-'));
    subtest.after(() => rmSync(cwd, { recursive: true, force: true }));
    const result = guard({ cwd, env: { PATH: process.env.PATH, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1', GIT_CEILING_DIRECTORIES: tmpdir(), LC_ALL: 'C' } });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /not a git repository/i);
    assert.doesNotMatch(result.stdout, /No release tag/);
  });
});
