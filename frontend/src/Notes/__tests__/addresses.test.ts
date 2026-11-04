import { FILE_SCHEME, NOTE_SCHEME, parseFileAddress, parseNoteAddress } from 'Notes/addresses';

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

describe('parseFileAddress', () => {
  const source = '11111111-1111-4111-8111-111111111111';

  it('reads the decoded path of an image of the source', () => {
    expect(parseFileAddress(source, `${FILE_SCHEME}devops%2Fplan%20b.png`)).toBe('devops/plan b.png');
  });

  it.each([
    `${FILE_SCHEME}..%2F..%2Faccounts`,
    `${FILE_SCHEME}a%2F.%2Fb.png`,
    `${FILE_SCHEME}`,
    `${FILE_SCHEME}%E0%A4%A`,
  ])('refuses a path that leaves the source or is not one: %j', (address) => {
    expect(parseFileAddress(source, address)).toBeNull();
  });
});
