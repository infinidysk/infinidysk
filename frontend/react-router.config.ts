import type { Config } from "@react-router/dev/config";
import { urlBaseFromEnv } from "./app/utils/url-base";

export default {
  // Server-side render by default, to enable SPA mode set this to `false`
  ssr: true,
  // NZBDAV_URL_BASE (or bare URL_BASE), read at build time, controls the React
  // Router basename so <Link> and useFetcher generate the correct paths when
  // the app is hosted under a sub-path. Must match the runtime env var — see
  // server.ts, which enforces the pairing.
  basename: urlBaseFromEnv(process.env) || "/",
} satisfies Config;
