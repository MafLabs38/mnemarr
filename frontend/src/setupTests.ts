import '@testing-library/jest-dom/vitest';

// PageContent puts its title in the browser tab, which is shared by every test of a file.
afterEach(() => {
  document.title = '';
});
