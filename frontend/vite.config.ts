import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";
import { urlBaseFromEnv } from "./app/utils/url-base";

function resolveAllowedHosts(): string[] {
  const raw = process.env["VITE_ALLOWED_HOSTS"]?.trim();
  if (!raw) return [".net"];
  return raw
    .split(",")
    .map((host) => host.trim())
    .filter(Boolean);
}

export default defineConfig(() => {
  // NZBDAV_URL_BASE (or bare URL_BASE), read at build time. `token` is the
  // "" / "/path" form baked into __URL_BASE__; Vite's `base` needs the
  // trailing-slash variant.
  const token = urlBaseFromEnv(process.env);
  return {
    base: token === "" ? "/" : `${token}/`,
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
    plugins: [tailwindcss(), reactRouter()],
  };
});
