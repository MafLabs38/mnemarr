import { copyText } from 'Utilities/Clipboard/copyText';

describe('copyText', () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, 'clipboard');
    Reflect.deleteProperty(document, 'execCommand');
  });

  it('uses the clipboard of the browser when the page may', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    expect(await copyText('a line')).toBe(true);
    expect(writeText).toHaveBeenCalledWith('a line');
  });

  it('copies from a selected field when the page is not in a secure context, as one served over plain HTTP is not', async () => {
    let copied = '';
    document.execCommand = vi.fn(() => {
      copied = document.querySelector('textarea')?.value ?? '';
      return true;
    });

    expect(await copyText('a line to paste')).toBe(true);
    expect(copied).toBe('a line to paste');
    expect(document.querySelector('textarea')).toBeNull();
  });

  it('tries the other way when the clipboard refuses', async () => {
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: vi.fn().mockRejectedValue(new Error('not allowed')) },
      configurable: true,
    });
    document.execCommand = vi.fn(() => true);

    expect(await copyText('x')).toBe(true);
  });

  it('says so when nothing can copy, and leaves nothing behind in the page', async () => {
    document.execCommand = vi.fn(() => false);
    expect(await copyText('x')).toBe(false);

    document.execCommand = vi.fn(() => {
      throw new Error('blocked');
    });
    expect(await copyText('x')).toBe(false);

    Reflect.deleteProperty(document, 'execCommand');
    expect(await copyText('x')).toBe(false);
    expect(document.querySelector('textarea')).toBeNull();
  });
});
