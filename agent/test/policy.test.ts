import assert from 'node:assert/strict';
import { test } from 'node:test';

import { classify, knownReadTools, shortToolName } from '../src/policy.ts';

test('every tool that is allowed on the command line is a read for the policy too', () => {
  for (const tool of knownReadTools) {
    assert.equal(classify(tool), 'read', tool);
  }
});

test('reads are told by their names; every other Mnemarr tool waits for the person', () => {
  for (const tool of ['search', 'get_status', 'list_sources', 'find_notes', 'read_note']) {
    assert.equal(classify(`mcp__mnemarr__${tool}`), 'read', tool);
  }
  for (const tool of [
    'create_note',
    'update_note',
    'delete_note',
    'create_source',
    'delete_source',
    'index_source',
    'sync_source',
    'request_collector_enrolment',
    'searching',
  ]) {
    assert.equal(classify(`mcp__mnemarr__${tool}`), 'write', tool);
  }
});

test('anything that is not a tool of Mnemarr is forbidden, whatever its name looks like', () => {
  for (const tool of [
    'Bash',
    'Read',
    'mcp__other__search',
    'mcp__mnemarr__',
    'mcp__mnemarr__Search',
    'mcp__mnemarr__../list_notes',
    'mcp__mnemarr__list_notes ',
  ]) {
    assert.equal(classify(tool), 'forbidden', tool);
  }
});

test('people see the short name of a tool', () => {
  assert.equal(shortToolName('mcp__mnemarr__create_note'), 'create_note');
  assert.equal(shortToolName('Bash'), 'Bash');
});
