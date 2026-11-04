import type { LucideIcon } from 'lucide-react';

import { icons } from 'Helpers/Props/icons';
import type { SourceKind } from 'Sources/sources';
import type { TranslationKey } from 'Utilities/String/en';

/** What the interface says and draws for each kind of source: one place, so that a kind looks the same everywhere (tree, note, badges). */
interface KindLook {
  icon: LucideIcon;
  title: TranslationKey;
  purpose: TranslationKey;
  /** The class that sets the colour of the kind (`--kind`). */
  className: 'kindMemory' | 'kindKnowledge' | 'kindApplicationDocs';
}

/** Adding a kind is a compile error until it has its look. */
export const kindLook: Record<SourceKind, KindLook> = {
  Memory: { icon: icons.KIND_MEMORY, title: 'MemoryTitle', purpose: 'MemoryNotesPurpose', className: 'kindMemory' },
  Knowledge: {
    icon: icons.KIND_KNOWLEDGE,
    title: 'KnowledgeTitle',
    purpose: 'KnowledgeNotesPurpose',
    className: 'kindKnowledge',
  },
  ApplicationDocs: {
    icon: icons.KIND_APPLICATION_DOCS,
    title: 'ApplicationDocsTitle',
    purpose: 'ApplicationDocsNotesPurpose',
    className: 'kindApplicationDocs',
  },
};

/** The order the kinds are shown in: the memory first, then what people wrote, then what applications bring. */
export const kindOrder: ReadonlyArray<SourceKind> = ['Memory', 'Knowledge', 'ApplicationDocs'];
