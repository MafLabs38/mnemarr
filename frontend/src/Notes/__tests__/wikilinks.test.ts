import { linkWikilinks, normalizeTarget, resolveRelative } from 'Notes/wikilinks';

describe('normalizeTarget', () => {
  it.each([
    ['Docker', 'docker'],
    [String.raw`  Devops\Docker.MD#Images `, 'devops/docker'],
    ['/devops/docker/', 'devops/docker'],
    ['notes.markdown', 'notes'],
  ])('reads %j as %j, the way the server does', (target, expected) => {
    expect(normalizeTarget(target)).toBe(expected);
  });

  it.each(['', '   ', '#only-a-heading', 'x'.repeat(201)])('reads %j as nothing to point at', (target) => {
    expect(normalizeTarget(target)).toBeNull();
  });
});

describe('linkWikilinks', () => {
  it('turns a wikilink into a link with its text, or the name when it has none', () => {
    expect(linkWikilinks('See [[Docker]] and [[devops/K8s|the cluster]].')).toBe(
      'See [Docker](mnemarr-note:docker) and [the cluster](mnemarr-note:devops%2Fk8s).',
    );
  });

  it('turns an embedded image into an image and an embedded note into a link', () => {
    expect(linkWikilinks('![[plan.PNG|300]] ![[Other note]]')).toBe(
      '![plan.PNG](mnemarr-file:plan.PNG) [Other note](mnemarr-note:other%20note)',
    );
  });

  it('escapes the brackets of a text so that it cannot break out of the link', () => {
    expect(linkWikilinks('[[x|a ](javascript:alert(1)) [b]]')).toBe(
      String.raw`[a \](javascript:alert(1)) \[b](mnemarr-note:x)`,
    );
  });

  it('leaves code, fenced or inline, and a link that names nothing as they are', () => {
    const text = 'Use `[[code]]` here, [[#heading]] and [[ ]]\n```\n[[fenced]]\n```\n[[after]]';
    expect(linkWikilinks(text)).toBe(
      'Use `[[code]]` here, [[#heading]] and [[ ]]\n```\n[[fenced]]\n```\n[after](mnemarr-note:after)',
    );
  });

  it('leaves an unclosed link alone', () => {
    expect(linkWikilinks('[[never closed')).toBe('[[never closed');
  });
});

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
