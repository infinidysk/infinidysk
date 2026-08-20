import { type RouteConfig } from "@react-router/dev/routes";
import { flatRoutes } from "@react-router/fs-routes";

export default [
  ...(await flatRoutes()),
  {
    path: "/explore/*",
    file: "routes/explore/route.tsx",
  },
] satisfies RouteConfig;
