import type { Route } from "./+types/route";
import { useState } from "react";
import { backendClient } from "~/clients/backend-client.server";
import { Form, redirect, useNavigation } from "react-router";
import { isAuthenticated, setSessionUser } from "~/auth/authentication.server";
import { Alert, Button, Input, Spinner } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

type OnboardingPageData = {
    error: string
}

export async function loader({ request }: Route.LoaderArgs) {
    if (await isAuthenticated(request)) return redirect("/")

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
    let submitButtonText = "Register";
    if (isLoading) {
        submitButtonDisabled = true;
        submitButtonText = "Registering...";
    } else if (username == "") {
        submitButtonDisabled = true;
        submitButtonText = "Username is required";
    } else if (password === "") {
        submitButtonDisabled = true;
        submitButtonText = "Password is required";
    } else if (password != confirmPassword) {
        submitButtonDisabled = true;
        submitButtonText = "Passwords must match";
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
                                <h1 className="bg-gradient-to-r from-primary to-success bg-clip-text text-2xl font-bold tracking-tight text-transparent">
                                    InfiniDysk
                                </h1>
                                <p className="mt-1 text-xs font-medium tracking-wide text-base-content/50">
                                    The NzbDAV SuperFork
                                </p>
                                <p className="mt-1 text-sm text-base-content/60">Set up your administrator account</p>
                            </div>
                        </div>

                        {pageData.error &&
                            <Alert variant="danger">
                                {pageData.error}
                            </Alert>
                        }
                        {!pageData.error &&
                            <Alert variant="warning">
                                <p className="mb-1 font-semibold">Welcome!</p>
                                Create credentials for managing your InfiniDysk server.
                            </Alert>
                        }

                        <fieldset className="fieldset space-y-3">
                            <label className="floating-label">
                                <span>Username</span>
                                <Input
                                    className="w-full"
                                    autoFocus
                                    name="username"
                                    type="text"
                                    placeholder="Choose a username"
                                    autoComplete="username"
                                    value={username}
                                    onChange={e => setUsername(e.currentTarget.value)}
                                />
                            </label>
                            <label className="floating-label">
                                <span>Password</span>
                                <Input
                                    className="w-full"
                                    name="password"
                                    type="password"
                                    placeholder="Choose a password"
                                    autoComplete="new-password"
                                    value={password}
                                    onChange={e => setPassword(e.currentTarget.value)}
                                />
                            </label>
                            <label className="floating-label">
                                <span>Confirm password</span>
                                <Input
                                    className="w-full"
                                    type="password"
                                    placeholder="Repeat your password"
                                    autoComplete="new-password"
                                    value={confirmPassword}
                                    onChange={e => setConfirmPassword(e.currentTarget.value)}
                                />
                            </label>
                        </fieldset>

                        <Button
                            className="w-full"
                            type="submit"
                            size="medium"
                            variant="primary"
                            disabled={submitButtonDisabled}>
                            {isLoading && <Spinner />}
                            {submitButtonText}
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
        if (await isAuthenticated(request)) return redirect("/")

        const isOnboarding = await backendClient.isOnboarding();
        if (!isOnboarding) return redirect("/login");

        const formData = await request.formData();
        const username = formData.get("username")?.toString();
        const password = formData.get("password")?.toString();
        if (!username || !password) throw new Error("username and password required");
        const isSuccess = await backendClient.createAccount(username, password);
        if (!isSuccess) throw new Error("Unknown error creating account");
        const responseInit = await setSessionUser(request, username);
        return redirect("/", responseInit);
    }
    catch (error) {
        if (error instanceof Error) {
            return { error: error.message };
        }
        throw error
    }
}
