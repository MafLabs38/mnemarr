import { machineNameFor } from 'Machines/machines';

describe('machineNameFor', () => {
  it.each([
    ['Quimper Gaming', 'quimper-gaming'],
    ['quimper-gaming', 'quimper-gaming'],
    ['  PC du salon  ', 'pc-du-salon'],
    ['Café été', 'cafe-ete'],
    ['WIN_PC.local', 'win_pc.local'],
    ['--a  b--', 'a-b'],
    ['***', ''],
    ['', ''],
  ])('makes %j into %j', (typed, name) => {
    expect(machineNameFor(typed)).toBe(name);
  });

  it('keeps a name within what the server accepts', () => {
    const name = machineNameFor('a'.repeat(80));
    expect(name).toHaveLength(50);
    expect(/^[a-z0-9][a-z0-9._-]{0,49}$/.test(name)).toBe(true);
  });
});
