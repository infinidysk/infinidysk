import { QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { type ComponentType, lazy, StrictMode, Suspense } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, createRoutesFromElements, Navigate, Outlet, Route, RouterProvider } from "react-router";
import { HttpError } from "./api/client";
import { authKey, Gate, Spinner } from "./auth";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import "./theme";
import "./index.css";

// Split per page so the sign-in screen does not wait for the whole app.
const named = <K extends string>(load: () => Promise<Record<K, ComponentType>>, name: K) =>
  lazy(() => load().then((module) => ({ default: module[name] })));
const Layout = named(() => import("./layout"), "Layout");
const Overview = named(() => import("./pages/overview"), "Overview");
const Queue = named(() => import("./pages/queue"), "Queue");
const SearchPage = named(() => import("./pages/search"), "SearchPage");
const Explorer = named(() => import("./pages/explorer"), "Explorer");
const Health = named(() => import("./pages/health"), "Health");
const Stats = named(() => import("./pages/stats"), "Stats");
const Settings = named(() => import("./pages/settings"), "Settings");
const System = named(() => import("./pages/system"), "System");
const TracePage = named(() => import("./pages/traces"), "TracePage");

// A tab from an older build asks for page chunks the server no longer has.
addEventListener("vite:preloadError", () => window.location.reload());

const client: QueryClient = new QueryClient({
  queryCache: new QueryCache({
    // An expired session drops back to the login form.
    onError: (error) => {
      if (error instanceof HttpError && error.status === 401) {
        void client.invalidateQueries({ queryKey: authKey });
      }
    },
  }),
  defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
});

const router = createBrowserRouter(
  createRoutesFromElements(
    <Route
      element={
        <Gate>
          <Suspense fallback={<Spinner />}>
            <Outlet />
          </Suspense>
        </Gate>
      }
    >
      <Route element={<Layout />}>
        <Route index element={<Navigate to="/overview" replace />} />
        <Route path="overview" element={<Overview />} />
        <Route path="queue" element={<Queue />} />
        <Route path="search" element={<SearchPage />} />
        <Route path="explorer" element={<Explorer />} />
        <Route path="health" element={<Health />} />
        <Route path="stats" element={<Stats />} />
        <Route path="providers" element={<Navigate to="/settings/providers" replace />} />
        <Route path="settings/:category?" element={<Settings />} />
        <Route path="system" element={<System />} />
        <Route path="system/traces/:id" element={<TracePage />} />
        <Route path="*" element={<Navigate to="/overview" replace />} />
      </Route>
    </Route>,
  ),
);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={client}>
      <TooltipProvider>
      <RouterProvider router={router} />
      <Toaster position="bottom-right" />
      </TooltipProvider>
    </QueryClientProvider>
  </StrictMode>,
);
