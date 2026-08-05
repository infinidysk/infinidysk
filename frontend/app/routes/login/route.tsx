import type { Route } from "./+types/route";
import { isAuthenticated, login } from "~/auth/authentication.server";
import { Form, redirect, useNavigation } from "react-router";
import { backendClient } from "~/clients/backend-client.server";
import { Alert, Button, Icon, Input, Spinner } from "~/components/ui";
import { isOidcEnabled } from "../../../server/oidc.server";
import { withUrlBase } from "~/utils/url-base";

type LoginPageData = {
    loginError: string | null;
    isOidcEnabled: boolean;
}

export async function loader({ request }: Route.LoaderArgs) {
    if (await isAuthenticated(request)) return redirect("/");

    const oidcEnabled = isOidcEnabled();
    if (!oidcEnabled) {
        const isOnboarding = await backendClient.isOnboarding();
        if (isOnboarding) return redirect("/onboarding");
    }

    const error = new URL(request.url).searchParams.get("error");
    return {
        loginError: getOidcErrorMessage(error),
        isOidcEnabled: oidcEnabled,
    } satisfies LoginPageData;
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
    const navigation = useNavigation();
    const isLoading = navigation.state == "submitting";
    const loginError = actionData?.loginError ?? loaderData.loginError;
    const showError = !!loginError;
    const submitButtonDisabled = isLoading;
    const submitButtonText = isLoading ? "Logging in..." : "Login";

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
                                <h1 className="bg-gradient-to-r from-primary to-success bg-clip-text text-2xl font-bold tracking-tight text-transparent">
                                    InfiniDysk
                                </h1>
                                <p className="mt-1 text-xs font-medium tracking-wide text-base-content/50">
                                    The NzbDAV SuperFork
                                </p>
                                <p className="mt-1 text-sm text-base-content/60">Sign in to manage your server</p>
                            </div>
                        </div>

                        {showError && <Alert variant="danger">{loginError}</Alert>}

                        <fieldset className="fieldset space-y-3">
                            <label className="floating-label">
                                <span>Username</span>
                                <Input
                                    className="w-full"
                                    name="username"
                                    type="text"
                                    placeholder="Enter your username"
                                    autoComplete="username"
                                    autoFocus
                                />
                            </label>
                            <label className="floating-label">
                                <span>Password</span>
                                <Input
                                    className="w-full"
                                    name="password"
                                    type="password"
                                    placeholder="Enter your password"
                                    autoComplete="current-password"
                                />
                            </label>
                        </fieldset>

                        <Button
                            className="w-full"
                            type="submit"
                            size="medium"
                            variant="primary"
                            disabled={submitButtonDisabled}
                        >
                            {isLoading && <Spinner />}
                            {submitButtonText}
                        </Button>

                        {loaderData.isOidcEnabled && (
                            <>
                                <div className="divider my-0 text-xs uppercase text-base-content/50">
                                    or
                                </div>
                                <a
                                    className="btn btn-outline w-full gap-2"
                                    href={withUrlBase("/auth/oidc/login")}
                                >
                                    <Icon name="login" className="!text-[18px]" />
                                    Sign in with SSO
                                </a>
                            </>
                        )}
                    </div>
                </Form>
            </div>
            </div>
        </main>
    );
}

function getOidcErrorMessage(error: string | null): string | null {
    if (error === "oidc_not_configured") {
        return "OIDC sign-in is not configured. Use your local account instead.";
    }
    if (error === "oidc_failed") {
        return "SSO sign-in failed. Try again or use your local account.";
    }
    return null;
}

export async function action({ request }: Route.ActionArgs) {
    try {
        const responseInit = await login(request);
        return redirect("/", responseInit);
    }
    catch (error) {
        if (error instanceof Error) {
            return { loginError: error.message };
        }
        throw error;
    }
}
