import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

function resolveAllowedHosts(): string[] {
  const raw = process.env.VITE_ALLOWED_HOSTS?.trim();
  if (!raw) return [".net"];
  return raw.split(",").map((host) => host.trim()).filter(Boolean);
}

// Mirror of normalizeUrlBase in app/utils/url-base.ts (this file runs outside the
// Vite app graph, so the helper cannot be imported). Produces:
//   - `viteBase`: Vite's `base` option form, "/" or "/path/" (trailing slash required).
//   - `token`: value baked into `__URL_BASE__` for browser code, "" or "/path" (no slash).
function normalizeUrlBase(raw: string | undefined): { viteBase: string; token: string } {
  if (!raw) return { viteBase: "/", token: "" };
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return { viteBase: "/", token: "" };
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  const withoutTrailing = withLeading.replace(/\/+$/, "");
  return { viteBase: `${withoutTrailing}/`, token: withoutTrailing };
}

export default defineConfig(() => {
  const { viteBase, token } = normalizeUrlBase(process.env.URL_BASE);
  return {
    base: viteBase,
    server: {
      allowedHosts: resolveAllowedHosts(),
    },
    resolve: {
      tsconfigPaths: true,
    },
    environments: {
      ssr: {
        build: {
          rollupOptions: {
            input: "./server/app.ts",
          },
        },
      },
    },
    define: {
      __URL_BASE__: JSON.stringify(token),
    },
    plugins: [
      tailwindcss(),
      reactRouter(),
    ],
  };
});
