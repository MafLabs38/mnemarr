# 0044 — The menu says which environment it is: a name, a logo and the version

- **Status**: accepted
- **Date**: 2026-11-13
- **Spec**: D-65; follows the identity badge of Portarr (its D-91), so that the three products say the same thing in the same place

## Context

The sandbox, the test host and the production Mnemarr look the same, and a person who has several tabs open does not know which one they are in, nor which version runs. Portarr already solves it: a small block at
the bottom of the menu with the name of the **laboratory** (the environment, not the application), a picture and the version, in the accent color. The maintainer asked for exactly the same in Mnemarr, with the
same style, and for it to be the lab's name and picture ("MafLabs", the maintainer's own avatar), not the product's: the product already has its name and its drawing in the header.

## Decision

1. **A badge at the bottom of the menu, the same as Portarr's.** The name (display font, 600), the image (at most 96 px, rounded corners, no card or border) and the version (`v0.6.0`, mono, small), straight on
   the sidebar, left-aligned, in a column that is as high as the window and pinned under the header, so that the badge stays at the bottom of the visible window while the page scrolls and the menu scrolls on its own.
   Under 768 px the menu is a bar and only the image remains, the texts stay available to assistive technology. The texts take the accent color (the color of the active entry) unless a color is set.
2. **Three layers, the nearest wins.** What an **administrator** sets in the interface (Settings, General, Identity: the name, the two colors, an uploaded image), then what the **deployment** says (`Identity:Name`,
   `Identity:Colors:Name`, `Identity:Colors:Version`, `Identity:LogoFile`: a file mounted into the container, checked like an upload), then the **product's own** (its name, its drawing, the accent). A part that is
   sent empty goes back to the layer below; a part that is missing is left alone. The deployment layer is what makes it one setting per environment, in the repository that describes the environment (GitOps), and
   what keeps the personal picture out of this public repository.
3. **Everything that is given is checked.** The name is one line of at most 40 characters; a color is `#rgb`, `#rrggbb` or one of the theme tokens (`accent`, `accentDeep`, `secondary`, `ink`, `inkSoft`,
   `inkMuted`): nothing else, so that a value never carries CSS (the interface checks it again before it puts it in a style). The image is a png, a webp or an svg of at most 1 MiB, **read by its content**: the signature
   of a png or a webp; an svg is well formed, has the SVG root, and no script, event handler, `foreignObject`, link to another document, DTD or style that loads from outside. A logo that fails is not used and
   the reason is shown (never its path). The same rules as Portarr's.
4. **The image is served like an icon.** `GET /api/v1/identity/logo?v=<hash>` (sign-in required, like the rest) with the hash of the file in its address: `private, max-age=31536000, immutable` with the hash, five
   minutes without it, an ETag, `nosniff` and `default-src 'none'; sandbox`. The general headers of the host (no cache, the application's content policy) leave alone what a response has already said, so that this
   image can be cached by the browser of the person while every other answer of the API is not.
5. **Anyone who is signed in reads it, an administrator changes it.** `GET /api/v1/identity` (name, colors, logo address, version, why a logo is not used) answers every role: the menu shows it to everyone. A change
   (`PUT /api/v1/identity`, `PUT` and `DELETE /api/v1/identity/logo`) is the new permission **`settings.manage`**, which only `Admin` holds (everyone sees it: it is an administrator's), and is audited
   (`identity.updated`, `identity.logo.updated`, `identity.logo.removed`; the values are not written in the log). The identity is one row of the database (the logo in it, at most 1 MiB), so it is backed up with it.
6. **The version follows the system status** where the role may read it (the badge asks every 30 seconds, so a new version shows up without a reload) and is the one the identity carries elsewhere.
7. **A Settings page, for now with one card.** The menu entry that was marked "Soon" is built: Settings, General, the Identity card (name, colors, logo, and the way back to the default logo), visible to those who hold `settings.manage`.

## Consequences

- The product's name stays in the header. A deployment that sets nothing looks as before, with the badge saying "Mnemarr", its drawing and its version.
- A new deployment has a place for its personal values (the name and a mounted file) and no need for the interface; the interface is for changing them without a deployment.
- A logo uploaded in the interface is in the database: it follows a restore, not a redeploy; removing it brings back the layer of the deployment.
- Not done: a color picker (a hex or a token is typed), several languages for the name, an identity per account.
