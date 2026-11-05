/**
 * Copies a text to the clipboard. The modern way (`navigator.clipboard`) exists only on a page served over HTTPS or from `localhost`: a Mnemarr
 * served over plain HTTP at home would never copy. Then the text is put in a field that is selected and copied, which every browser still allows
 * from a click. Says whether the text was copied: when it was not, the person is asked to copy it by hand.
 */
export async function copyText(text: string): Promise<boolean> {
  if (navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Refused (a permission, a frame): the other way is tried.
    }
  }
  return copyBySelection(text);
}

function copyBySelection(text: string): boolean {
  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.position = 'fixed';
  field.style.opacity = '0';
  document.body.append(field);
  field.select();
  try {
    // Old, and the only way that works without a secure context.
    return typeof document.execCommand === 'function' && document.execCommand('copy');
  } catch {
    return false;
  } finally {
    field.remove();
  }
}
