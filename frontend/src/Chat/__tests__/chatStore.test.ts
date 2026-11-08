import { act, renderHook } from '@testing-library/react';

import {
  clampWidth,
  defaultPanelWidth,
  maxPanelWidth,
  minPanelWidth,
  resetChatStore,
  useChatStore,
} from '../chatStore';

describe('chatStore', () => {
  beforeEach(() => {
    localStorage.clear();
    resetChatStore();
  });

  it('keeps the panel closed, at its default width and with no conversation at first', () => {
    const { result } = renderHook(() => useChatStore());

    expect(result.current).toMatchObject({
      open: false,
      width: defaultPanelWidth,
      conversationId: null,
      agent: null,
      model: null,
    });
  });

  it('keeps what the person chose in the browser, and can be read by the part of it a component needs', () => {
    const { result } = renderHook(() => useChatStore());
    const open = renderHook(() => useChatStore((state) => state.open));

    act(() => {
      result.current.setOpen(true);
      result.current.setConversation('c1');
      result.current.setAgent('openai', 'gpt-4.1');
    });

    expect(open.result.current).toBe(true);
    expect(JSON.parse(localStorage.getItem('mnemarr.chat') ?? '{}')).toMatchObject({
      open: true,
      conversationId: 'c1',
      agent: 'openai',
      model: 'gpt-4.1',
    });
  });

  it('keeps the width between its limits', () => {
    expect(clampWidth(10)).toBe(minPanelWidth);
    expect(clampWidth(5000)).toBe(maxPanelWidth);
    expect(clampWidth(500.4)).toBe(500);
  });

  it('works when the browser keeps nothing', () => {
    const failing = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('full');
    });
    const { result } = renderHook(() => useChatStore());

    act(() => result.current.setOpen(true));

    expect(result.current.open).toBe(true);
    failing.mockRestore();
  });
});
