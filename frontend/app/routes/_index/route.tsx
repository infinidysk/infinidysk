import { redirect } from "react-router";

export function loader() {
    return redirect("/overview")
}

export function action() {
    return new Response("Method Not Allowed", {
        status: 405,
        headers: { Allow: "GET, HEAD" },
    });
}
