import type { Route } from "./+types/route";
import { useState } from "react";
import { backendClient } from "~/clients/backend-client.server";
import { Form, redirect, useNavigation } from "react-router";
import { isAuthenticated, setSessionUser } from "~/auth/authentication.server";
import { Alert, Button, Input, Spinner } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

export async function loader({ request }: Route.LoaderArgs) {
  if (await isAuthenticated(request)) return redirect("/");

  const isOnboarding = await backendClient.isOnboarding();
  if (!isOnboarding) return redirect("/login");

  return { error: null };
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
  const pageData = actionData || loaderData;
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  const navigation = useNavigation();
  const isLoading = navigation.state == "submitting";

  let submitButtonDisabled = false;
  if (isLoading) {
    submitButtonDisabled = true;
  } else if (username == "") {
    submitButtonDisabled = true;
  } else if (password === "") {
    submitButtonDisabled = true;
  } else if (password != confirmPassword) {
    submitButtonDisabled = true;
  }

  return (
    <main className="flex min-h-dvh flex-col bg-base-300">
      <div className="hero flex-1">
        <div className="hero-content w-full max-w-sm px-4 py-8">
          <Form
            className="card w-full border border-base-content/10 bg-base-100 shadow-xl"
            method="POST"
          >
            <div className="card-body gap-5">
              <div className="flex flex-col items-center gap-3 text-center">
                <img
                  className="h-16 w-16 rounded-2xl bg-gradient-to-br from-primary via-info to-success p-0.5 shadow-lg shadow-primary/20"
                  src={withUrlBase("/logo.png")}
                  alt="InfiniDysk"
                />
                <div>
                  <h1 className="text-2xl font-bold tracking-tight text-primary">InfiniDysk</h1>
                  <p className="mt-1 text-xs font-medium tracking-wide text-base-content/50">
                    The NzbDAV SuperFork
                  </p>
                  <p className="mt-1 text-sm text-base-content/60">
                    Set up your administrator account
                  </p>
                </div>
              </div>

              {pageData.error && <Alert variant="danger">{pageData.error}</Alert>}
              {!pageData.error && (
                <Alert variant="info">
                  <div>
                    <p className="font-semibold">Welcome!</p>
                    <p>Create credentials for managing your InfiniDysk server.</p>
                  </div>
                </Alert>
              )}

              <fieldset className="fieldset space-y-3">
                <div className="space-y-1">
                  <label className="floating-label validator w-full">
                    <Input
                      className="w-full"
                      autoFocus
                      name="username"
                      type="text"
                      required
                      placeholder="Choose a username"
                      autoComplete="username"
                      value={username}
                      onChange={(e) => setUsername(e.currentTarget.value)}
                    />
                    <span>Username</span>
                  </label>
                  <p className="validator-hint">Username is required.</p>
                </div>
                <div className="space-y-1">
                  <label className="floating-label validator w-full">
                    <Input
                      className="w-full"
                      name="password"
                      type="password"
                      required
                      minLength={1}
                      placeholder="Choose a password"
                      autoComplete="new-password"
                      value={password}
                      onChange={(e) => setPassword(e.currentTarget.value)}
                    />
                    <span>Password</span>
                  </label>
                  <p className="validator-hint">Password is required.</p>
                </div>
                <div className="space-y-1">
                  <label className="floating-label w-full">
                    <Input
                      className={`w-full ${password !== confirmPassword ? "input-error" : ""}`}
                      name="confirmPassword"
                      type="password"
                      required
                      placeholder="Repeat your password"
                      autoComplete="new-password"
                      value={confirmPassword}
                      onChange={(e) => setConfirmPassword(e.currentTarget.value)}
                    />
                    <span>Confirm password</span>
                  </label>
                  {password !== "" && confirmPassword === "" && (
                    <p className="text-xs text-error">Confirm your password.</p>
                  )}
                  {password !== confirmPassword && confirmPassword !== "" && (
                    <p className="text-xs text-error">Passwords must match.</p>
                  )}
                </div>
              </fieldset>

              <Button
                className="w-full"
                type="submit"
                size="medium"
                variant="primary"
                disabled={submitButtonDisabled}
              >
                {isLoading && <Spinner />}
                {isLoading ? "Registering..." : "Register"}
              </Button>
              <p className="text-center text-xs text-base-content/50">
                First-time setup · this account becomes the administrator
              </p>
            </div>
          </Form>
        </div>
      </div>
    </main>
  );
}

export async function action({ request }: Route.ActionArgs) {
  try {
    if (await isAuthenticated(request)) return redirect("/");

    const isOnboarding = await backendClient.isOnboarding();
    if (!isOnboarding) return redirect("/login");

    const formData = await request.formData();
    const username = formData.get("username");
    const password = formData.get("password");
    const confirmPassword = formData.get("confirmPassword");
    if (typeof username !== "string" || typeof password !== "string" || !username || !password)
      throw new Error("username and password required");
    if (typeof confirmPassword !== "string" || confirmPassword !== password)
      throw new Error("Passwords must match.");
    const isSuccess = await backendClient.createAccount(username, password);
    if (!isSuccess) throw new Error("Unknown error creating account");
    const responseInit = await setSessionUser(request, username);
    return redirect("/setup", responseInit);
  } catch (error) {
    if (error instanceof Error) {
      return { error: error.message };
    }
    throw error;
  }
}
