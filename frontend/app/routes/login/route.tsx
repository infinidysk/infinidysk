import type { Route } from "./+types/route";
import { getSessionUser, isAuthenticated, selectViewerProfile } from "~/auth/authentication.server";
import {
  Form,
  Link,
  redirect,
  useNavigation,
  useRouteError,
  isRouteErrorResponse,
} from "react-router";
import { useState } from "react";
import { backendClient } from "~/clients/backend-client.server";
import { Alert, Button, Icon, Input, Spinner } from "~/components/ui";
import { ProfileAvatar } from "~/components/watch/profile-avatar";
import { withUrlBase } from "~/utils/url-base";

export async function loader({ request }: Route.LoaderArgs) {
  if (await isAuthenticated(request)) {
    const user = await getSessionUser(request);
    return redirect(user?.role === "profile" ? "/watch" : "/overview");
  }

  // A fresh install must finish onboarding before anybody can pick a profile.
  if (await backendClient.isOnboarding()) return redirect("/onboarding");

  try {
    return { profiles: await backendClient.listViewerProfiles(), loadError: null };
  } catch {
    return { profiles: [], loadError: "Profiles could not be loaded." };
  }
}

export async function action({ request }: Route.ActionArgs) {
  const formData = await request.formData();
  const intent = formData.get("intent");

  if (intent === "create") {
    const rawName = formData.get("name");
    const name = typeof rawName === "string" ? rawName : "";
    try {
      const profile = await backendClient.createViewerProfile(name);
      return redirect("/watch", await selectViewerProfile(request, profile));
    } catch (error) {
      return {
        intent: "create" as const,
        error: error instanceof Error ? error.message : "Could not create the profile.",
      };
    }
  }

  try {
    const profiles = await backendClient.listViewerProfiles();
    const selected = Number(formData.get("profileId"));
    const profile = profiles.find((entry) => entry.id === selected);
    if (!profile) return { intent: "select" as const, error: "That profile no longer exists." };
    return redirect("/watch", await selectViewerProfile(request, profile));
  } catch (error) {
    return {
      intent: "select" as const,
      error: error instanceof Error ? error.message : "Could not open the profile.",
    };
  }
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";
  const [creating, setCreating] = useState(false);
  const showCreateForm = creating || actionData?.intent === "create";
  const error = actionData?.error ?? loaderData.loadError;

  return (
    <main className="flex min-h-dvh flex-col bg-base-300">
      <div className="hero flex-1">
        <div className="hero-content w-full max-w-2xl px-4 py-8">
          <div className="w-full">
            <div className="flex flex-col items-center gap-3 text-center">
              <img
                className="h-16 w-16 rounded-2xl bg-gradient-to-br from-primary via-info to-success p-0.5 shadow-lg shadow-primary/20"
                src={withUrlBase("/logo.png")}
                alt="InfiniDysk"
              />
              <div>
                <p className="text-2xl font-bold tracking-tight text-primary">InfiniDysk</p>
                <p className="mt-1 text-xs font-medium tracking-wide text-base-content/50">
                  The NzbDAV SuperFork
                </p>
              </div>
              <h1 className="mt-2 text-xl font-semibold">Who&apos;s watching?</h1>
            </div>

            {error && (
              <div className="mt-6">
                <Alert variant="danger">{error}</Alert>
              </div>
            )}

            <div className="mt-6 grid grid-cols-2 gap-4 sm:grid-cols-3">
              {loaderData.profiles.map((profile) => (
                <Form method="post" key={profile.id} className="min-w-0">
                  <input type="hidden" name="intent" value="select" />
                  <input type="hidden" name="profileId" value={profile.id} />
                  <button
                    type="submit"
                    disabled={isSubmitting}
                    className="flex w-full flex-col items-center gap-2 rounded-box border border-base-content/10 bg-base-100 p-4 transition hover:border-primary/60 hover:bg-base-200"
                  >
                    <ProfileAvatar name={profile.name} className="h-16 w-16 text-2xl" />
                    <span className="w-full truncate text-sm font-medium">{profile.name}</span>
                  </button>
                </Form>
              ))}

              {showCreateForm ? (
                <Form
                  method="post"
                  className="col-span-2 flex min-w-0 flex-col gap-2 rounded-box border border-base-content/10 bg-base-100 p-4 sm:col-span-1"
                >
                  <input type="hidden" name="intent" value="create" />
                  <label className="floating-label">
                    <span>Profile name</span>
                    <Input
                      className="w-full"
                      name="name"
                      type="text"
                      placeholder="Enter a name"
                      maxLength={32}
                      required
                      autoFocus
                    />
                  </label>
                  <div className="flex items-center gap-2">
                    <Button className="flex-1" type="submit" size="small" disabled={isSubmitting}>
                      {isSubmitting && <Spinner />}
                      Create
                    </Button>
                    <Button
                      className="flex-1"
                      type="button"
                      size="small"
                      variant="ghost"
                      onClick={() => setCreating(false)}
                      disabled={isSubmitting}
                    >
                      Cancel
                    </Button>
                  </div>
                </Form>
              ) : (
                <Button
                  className="h-full min-h-28 w-full flex-col gap-2 border-dashed"
                  variant="outline"
                  onClick={() => setCreating(true)}
                >
                  <Icon name="add" className="!text-[32px]" />
                  Add profile
                </Button>
              )}
            </div>

            <div className="mt-8 flex justify-center">
              <Link
                to="/login/admin"
                aria-label="Admin sign in"
                className="btn btn-ghost btn-xs gap-1 text-base-content/40"
              >
                <Icon name="admin_panel_settings" className="!text-[16px]" />
              </Link>
            </div>
          </div>
        </div>
      </div>
    </main>
  );
}

export function ErrorBoundary() {
  const error = useRouteError();
  const isRejectedAction = isRouteErrorResponse(error) && error.status === 400;

  return (
    <main className="flex min-h-dvh flex-col bg-base-300">
      <div className="hero flex-1">
        <div className="hero-content w-full max-w-sm px-4 py-8">
          <div className="card w-full border border-base-content/10 bg-base-100 shadow-xl">
            <div className="card-body gap-5 text-center">
              <h1 className="text-2xl font-bold tracking-tight text-primary">
                Profiles unavailable
              </h1>
              <Alert variant="danger">
                {isRejectedAction
                  ? "The profile request could not be verified. Return to the profile picker and try again."
                  : "Something went wrong while loading profiles. Return to the profile picker and try again."}
              </Alert>
              <a className="btn btn-primary w-full" href={withUrlBase("/login")}>
                Return to profiles
              </a>
            </div>
          </div>
        </div>
      </div>
    </main>
  );
}
