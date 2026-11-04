# Mnemarr — Design and Frontend Guidelines

> **Status**: v1, 2026-11-01. Mandatory for all UI code (human- or AI-written).
> Mnemarr follows **[Portarr's DESIGN.md](https://github.com/MafLabs38/portarr/blob/98458ee1f80e140cf15adab2416a386970c7bfc6/docs/DESIGN.md)**, pinned to the commit of 2026-10-28 so that a later edit in another repository cannot change these rules without a review here (the *arr page anatomy, component vocabulary, design tokens, accessibility rules and pull request checklist). This page records what is specific to Mnemarr, or where the two differ. Where nothing is said here, the pinned document applies; the decisions it cites (D-xx, V7) are Portarr's own and do not apply here.

## 1. What is shared with Portarr

- The **page anatomy**: a header (56 px), a sidebar (220 px) and a page content with a heading, as in Sonarr.
- The **theme tokens**: same names and same values (`--paper`, `--paperCard`, `--ink`, `--accent`, `--shadowCard`…), so the products of the family look alike. They live in `frontend/src/Styles/Themes/themes.css`. A component never carries a hard-coded color, font, radius or shadow.
- The **fonts** (Fredoka, Nunito, JetBrains Mono), self-hosted through `@fontsource`: the interface makes no request to a third party.
- The **code conventions**: CSS Modules, one component per file, absolute imports from `src`, icons only through `Helpers/Props/icons.ts`, the `kind` vocabulary (`size` joins when a component needs it), every text through `translate()`.
- **Server data only through `useApiQuery`** (`Helpers/Hooks`), which goes through `apiGet` (`Helpers/Api`): never a `fetch` in a component. `apiGet` and `apiSend` are the one place where a URL base (SPEC Q-21) and the header of a write are added; a 401 is handled by the query client, which ends the session and forgets what it had read. **Writes** go through `apiSend` in a mutation hook written next to the feature's query hooks (`useCreateAccount`, `useSignIn`…), which also refreshes the queries it changes: a component keeps its form, never a path or a query key. The query client retries once, and not at all after a 401, 403 or 404.

## 2. What is specific to Mnemarr

### 2.1 The interface configures and watches the brain, and reads its notes

Decision D-18: Mnemarr's interface is minimal: it configures and watches the brain. Decision D-37 amends it with a read-only notes reader (a person reads the notes they may read, in the interface or in Obsidian); the interface never edits memory. Its menu is:

| Entry | Content | State |
|---|---|---|
| Overview | The home page: is Mnemarr online, and the state of the memory | built |
| Notes | The notes reader: the notes a person may read, rendered (D-37) | built |
| Sources | The Git repositories Mnemarr reads and writes | soon |
| Agents | The collector agents and their credentials | soon |
| Settings | Model providers, schedules, retention, masking | soon |
| System | Status, and later health, tasks, backup and logs | built (Status) |

An entry whose page is not built is **shown, marked "Soon", and is not a link** (`aria-disabled`). The whole shape of the product is visible, and nothing leads to a dead page. The entry becomes a link in the pull request that builds its page.

### 2.2 The brand

- The logo is a **profile of Mnemosyne, the Greek goddess of memory, whose brain is a circuit board** (the name of the product comes from her), supplied by the maintainer: `frontend/public/logo.png` for the header and the home page, `frontend/public/favicon.png` for the browser tab. The background was removed with the light areas of the drawing kept (the folds of the tunic), the images carry no metadata, and its colors are fixed (the brand), not tokens.
- It is **decorative** (`alt=""`): the product name is always next to it.
- Playfulness is for the home page and empty states ("Nothing to remember yet."), never for errors or alerts, as in Portarr.

### 2.3 Components specific to Mnemarr

| Component | Role | Notes |
|---|---|---|
| `BrandLogo` | The logo, from `public/logo.png` | Decorative (`alt=""`); height only, the drawing sets the width |
| `StatusLabel` | A state as icon, text and color | Never color alone. Each `kind` has its icon in a typed table: a new kind does not compile until it has one |
| Menu entry marked "Soon" | A page that is not built yet | Not a link; the word "Soon" stays for assistive technology on small screens |
| Skip link | First stop of the keyboard, to the content | Visible only when focused |

The "online" state of the Overview is the API answering ("API reachable"). It becomes the health of Mnemarr's checks when the health resource of F-HLT-01 exists.

Breakpoints are the literal `768px` in the CSS modules for now. Portarr declares them as `@custom-media`; Mnemarr adds that when PostCSS joins the build, and until then a single literal is easier to find than a second mechanism.

### 2.4 Not there yet

The light theme, the color-impaired mode, the chat panel and the settings forms arrive with the verticals that need them. Until then there is only the dark theme.

## 3. Pull request UI checklist

The checklist of Portarr's document applies, with this addition: a new menu entry is added to `frontend/src/App/navigation.ts` as unavailable, and turned on in the pull request that delivers its page.
