import { NOTE_SCHEME, parseNoteAddress } from 'Notes/addresses';

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
