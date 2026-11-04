import { NOTE_SCHEME, parseNoteAddress, resolveRelative } from 'Notes/addresses';

describe('resolveRelative', () => {
  it.each([
    ['devops/docker.md', 'k8s.md', 'devops/k8s.md'],
    ['devops/docker.md', '../index.md#top', 'index.md'],
    ['devops/docker.md', 'img/plan%20b.png', 'devops/img/plan b.png'],
    ['a.md', './b.md', 'b.md'],
  ])('resolves %j from %j', (note, reference, expected) => {
    expect(resolveRelative(note, reference)).toBe(expected);
  });

  it.each([
    ['a.md', '../outside.md'],
    ['devops/a.md', '../../outside.md'],
    ['a.md', '%E0%A4%A'],
    ['a.md', ''],
  ])('refuses to leave the source: %j from %j', (note, reference) => {
    expect(resolveRelative(note, reference)).toBeNull();
  });
});

describe('parseNoteAddress', () => {
  it('reads the source and the decoded path the server wrote', () => {
    expect(parseNoteAddress(`${NOTE_SCHEME}11111111-1111-4111-8111-111111111111/devops%2FOther%20note.md`)).toEqual({
      sourceId: '11111111-1111-4111-8111-111111111111',
      path: 'devops/Other note.md',
    });
  });

  it.each([
    `${NOTE_SCHEME}`,
    `${NOTE_SCHEME}no-slash`,
    `${NOTE_SCHEME}/path.md`,
    `${NOTE_SCHEME}id/%E0%A4%A`,
    'http://x/y',
  ])('refuses what the server could not have written: %j', (address) => {
    expect(parseNoteAddress(address)).toBeNull();
  });
});
