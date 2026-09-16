import type { Route } from "./+types/route";
import { logout } from "~/auth/authentication.server";
import { Button } from "~/components/ui";
import { Form, Link, redirect } from "react-router";

async function logoutAndRedirect(request: Request) {
  const responseInit = await logout(request);
  return redirect("/login", responseInit);
}

export function loader(_args: Route.LoaderArgs) {
  return null;
}

export async function action({ request }: Route.ActionArgs) {
  // if we logout intent is not confirmed, redirect to landing page
  const formData = await request.formData();
  const confirm = formData.get("confirm");
  if (confirm !== "true") return redirect("/");

  // otherwise, proceed to log out!
  return logoutAndRedirect(request);
}

export default function Logout() {
  return (
    <main className="flex min-h-dvh flex-col bg-base-300">
      <div className="hero flex-1">
        <div className="hero-content w-full max-w-sm px-4 py-8 text-center">
          <div>
            <h1 className="text-2xl font-bold text-primary">Log out?</h1>
            <p className="mt-2 text-base-content/60">
              You will need to sign in again to manage your server.
            </p>
            <Form method="post" className="mt-6 flex justify-center gap-3">
              <input name="confirm" value="true" type="hidden" />
              <Link className="btn btn-ghost" to="/">
                Cancel
              </Link>
              <Button variant="primary" type="submit">
                Log out
              </Button>
            </Form>
          </div>
        </div>
      </div>
    </main>
  );
}
