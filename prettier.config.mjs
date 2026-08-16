// Mirrors the odata2ts project standard (printWidth 120, sorted imports, formatted package.json), as the
// sibling repo server/cap does. Scoped to the test harness by .prettierignore: the documentation in this
// repo is hand-wrapped and the .http collection is not prettier's business.
export default {
  plugins: ["prettier-plugin-packagejson", "@ianvs/prettier-plugin-sort-imports"],
  printWidth: 120,
  tabWidth: 2,
  semi: true,
};
