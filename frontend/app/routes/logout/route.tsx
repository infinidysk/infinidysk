import type { Route } from "./+types/route";
import { logout } from "~/auth/authentication.server";
import { redirect } from "react-router";

async function logoutAndRedirect(request: Request) {
  const responseInit = await logout(request);
  return redirect("/login", responseInit);
}

export async function loader({ request }: Route.LoaderArgs) {
  return logoutAndRedirect(request);
}

export async function action({ request }: Route.ActionArgs) {
  // if we logout intent is not confirmed, redirect to landing page
  const formData = await request.formData();
  const confirm = formData.get("confirm");
  if (confirm !== "true") return redirect("/");

  // otherwise, proceed to log out!
  return logoutAndRedirect(request);
}
