import eslint from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";
import { importBoundariesPlugin } from "./eslint-rules/import-boundaries.mjs";

export default tseslint.config(
  {
    ignores: [
      "build/**",
      "dist-node/**",
      ".react-router/**",
      "node_modules/**",
      "coverage/**",
      "app/generated/**",
      // Build/compile outputs that may exist locally but are gitignored.
      "server.js",
      "server.d.ts",
      "vite.config.js",
      "vite.config.d.ts",
    ],
  },
  eslint.configs.recommended,
  // Type-aware rules: catch floating/misused promises and unsafe `any` flows.
  // Requires generated route types (`react-router typegen`) — the lint script
  // runs typegen first.
  ...tseslint.configs.recommendedTypeChecked,
  {
    plugins: {
      "react-hooks": reactHooks,
      "import-boundaries": importBoundariesPlugin,
    },
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
      globals: {
        console: "readonly",
        process: "readonly",
        Buffer: "readonly",
        __dirname: "readonly",
        __filename: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
        setInterval: "readonly",
        clearInterval: "readonly",
        fetch: "readonly",
        FormData: "readonly",
        File: "readonly",
        Blob: "readonly",
        URL: "readonly",
        URLSearchParams: "readonly",
        AbortController: "readonly",
        Request: "readonly",
        Response: "readonly",
        Headers: "readonly",
        WebSocket: "readonly",
        MessageEvent: "readonly",
        NodeJS: "readonly",
        document: "readonly",
        window: "readonly",
        localStorage: "readonly",
        sessionStorage: "readonly",
        HTMLElement: "readonly",
        customElements: "readonly",
        React: "readonly",
      },
    },
    rules: {
      // Enforce immediately — backlog cleared in this PR.
      "no-var": "error",

      // Plugin registered so existing eslint-disable comments resolve.
      // Full react-hooks recommended (incl. React Compiler rules) is a follow-up ratchet.
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "error",
      "import-boundaries/no-cross-feature-imports": "error",

      // Type-aware strictness (#853 phase 1). The four promise rules per the
      // issue; no-unsafe-* come from recommendedTypeChecked.
      "@typescript-eslint/no-floating-promises": "error",
      "@typescript-eslint/no-misused-promises": "error",
      "@typescript-eslint/await-thenable": "error",
      "@typescript-eslint/require-await": "error",
      "@typescript-eslint/no-explicit-any": "error",
      "@typescript-eslint/no-unused-vars": [
        "error",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
        },
      ],

      // no-undef is meaningless for TypeScript (the compiler owns undefined-name
      // detection) and misfires on type-only imports; typescript-eslint FAQ
      // recommends turning it off for TS files.
      "no-undef": "off",

      // Remaining recommended rules kept as errors now that the warning budget is zero.
      "@typescript-eslint/no-empty-object-type": "error",
      "@typescript-eslint/no-unused-expressions": "error",
      "no-unused-expressions": "off",
      "no-empty": "error",
      "no-extra-boolean-cast": "error",
      "no-useless-assignment": "error",
      "prefer-const": "error",
    },
  },
  {
    // Root config files are not part of any tsconfig project; lint them
    // without type information.
    files: ["*.js", "*.ts", "*.mjs", "eslint-rules/**", "scripts/**/*.mjs"],
    ...tseslint.configs.disableTypeChecked,
  },
);
